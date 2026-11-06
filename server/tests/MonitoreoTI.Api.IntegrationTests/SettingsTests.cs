using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Configuración de la empresa (W-40): datos y plan, apariencia, departamentos, seguridad y correo.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class SettingsTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];
    private AgentTestApi _api = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.ServerConnectionString is not null)
            _api = new AgentTestApi(database);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
    }

    private async Task<HttpClient> AdminAsync() => _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));

    private Task<object?> AuditAsync(string action) =>
        _api.ScalarAsync("SELECT TOP 1 detalle FROM dbo.auditoria WHERE accion = @a ORDER BY id DESC", ("@a", action));

    [DatabaseFact]
    public async Task Only_who_can_edit_settings_gets_in_but_everyone_sees_the_company_look()
    {
        var technician = _api.ClientWith(await _api.TokenForAsync(Company, "Tecnico"));
        (await technician.GetAsync("/api/v1/configuracion/empresa")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await technician.PutAsJsonAsync("/api/v1/configuracion/apariencia/color", new { color = "#123456" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await technician.GetAsync("/api/v1/apariencia")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DatabaseFact]
    public async Task The_company_data_is_validated_audited_and_shows_the_plan_usage()
    {
        var admin = await AdminAsync();
        var company = await admin.GetFromJsonAsync<JsonElement>("/api/v1/configuracion/empresa");
        company.GetProperty("name").GetString().Should().Be(Company);
        company.GetProperty("planName").GetString().Should().NotBeNullOrEmpty();
        company.GetProperty("users").GetProperty("used").GetInt32().Should().BeGreaterThan(0);
        company.GetProperty("branches").GetProperty("used").GetInt32().Should().BeGreaterThan(0);
        company.GetProperty("modules").EnumerateArray().Select(m => m.GetString()).Should().Contain("monitoreo");

        (await admin.PutAsJsonAsync("/api/v1/configuracion/empresa", new { nombre = Company, rnc = "1-01-12345-6" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PutAsJsonAsync("/api/v1/configuracion/empresa", new { nombre = Company, telefono = "abc" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await admin.PutAsJsonAsync("/api/v1/configuracion/empresa",
            new { nombre = Company, rnc = "101123456", correoContacto = "ti@caribe.test", telefono = "(809) 555-0100" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        company = await admin.GetFromJsonAsync<JsonElement>("/api/v1/configuracion/empresa");
        company.GetProperty("taxId").GetString().Should().Be("101123456");
        ((string?)await AuditAsync("configuracion.empresa")).Should().Contain("101123456");
    }

    [DatabaseFact]
    public async Task The_logo_must_be_a_real_small_image_and_the_color_a_hex()
    {
        var admin = await AdminAsync();
        try
        {
            // Un SVG (puede llevar código) o un texto con nombre de imagen no pasan: cuenta el contenido.
            var svg = Convert.ToBase64String("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray());
            (await admin.PutAsJsonAsync("/api/v1/configuracion/apariencia/logo", new { datos = svg })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var big = new byte[300 * 1024];
            Png.CopyTo(big, 0);
            (await admin.PutAsJsonAsync("/api/v1/configuracion/apariencia/logo", new { datos = Convert.ToBase64String(big) }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);

            (await admin.PutAsJsonAsync("/api/v1/configuracion/apariencia/logo", new { datos = Convert.ToBase64String(Png) }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PutAsJsonAsync("/api/v1/configuracion/apariencia/color", new { color = "rojo" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await admin.PutAsJsonAsync("/api/v1/configuracion/apariencia/color", new { color = "#1D4ED8" })).StatusCode.Should().Be(HttpStatusCode.OK);

            var look = await admin.GetFromJsonAsync<JsonElement>("/api/v1/apariencia");
            look.GetProperty("color").GetString().Should().Be("#1d4ed8");
            look.GetProperty("logo").GetString().Should().StartWith("data:image/png;base64,");
            // En la auditoría va el tipo y el tamaño, no la imagen.
            var audit = (string?)await AuditAsync("configuracion.logo");
            audit.Should().Contain("image/png").And.NotContain(Convert.ToBase64String(Png));

            (await admin.DeleteAsync("/api/v1/configuracion/apariencia/logo")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.PutAsJsonAsync("/api/v1/configuracion/apariencia/color", new { color = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
            look = await admin.GetFromJsonAsync<JsonElement>("/api/v1/apariencia");
            look.GetProperty("color").ValueKind.Should().Be(JsonValueKind.Null);
            look.GetProperty("logo").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            await _api.ExecuteAsync("UPDATE dbo.empresas SET logo = NULL, logo_tipo = NULL, color_primario = NULL WHERE nombre = @e", ("@e", Company));
        }
    }

    /// <summary>Departamentos por sucursal (decisión 79): se crean en la sucursal, el nombre se repite entre sucursales.</summary>
    [DatabaseFact]
    public async Task Departments_belong_to_a_branch_and_one_with_machines_cannot_be_deleted()
    {
        var admin = await AdminAsync();
        var name = $"Prueba {Guid.NewGuid():N}"[..20];
        var machine = (int)(await _api.ScalarAsync(
            "SELECT TOP 1 m.id FROM dbo.maquinas m JOIN dbo.empresas e ON e.id = m.empresa_id WHERE e.nombre = @e ORDER BY m.id", ("@e", Company)))!;
        var branch = (int)(await _api.ScalarAsync("SELECT sucursal_id FROM dbo.maquinas WHERE id = @m", ("@m", machine)))!;
        var other = (int)(await _api.ScalarAsync(
            "SELECT TOP 1 s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.id <> @b ORDER BY s.id",
            ("@e", Company), ("@b", branch)))!;

        var created = await admin.PostAsJsonAsync($"/api/v1/sucursales/{branch}/departamentos", new { nombre = name });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await admin.PostAsJsonAsync($"/api/v1/sucursales/{branch}/departamentos", new { nombre = name })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        // El mismo nombre en otra sucursal es otro departamento.
        var twin = await admin.PostAsJsonAsync($"/api/v1/sucursales/{other}/departamentos", new { nombre = name });
        twin.StatusCode.Should().Be(HttpStatusCode.Created);
        var twinId = (await twin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        // Responsable: solo un usuario de la empresa.
        (await admin.PutAsJsonAsync($"/api/v1/sucursales/{branch}/departamentos/{id}", new { nombre = name, responsableId = 987654 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // No se cambia un departamento por la ruta de otra sucursal.
        (await admin.PutAsJsonAsync($"/api/v1/sucursales/{other}/departamentos/{id}", new { nombre = name })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // El formulario del equipo solo ofrece los de su sucursal.
        var forBranch = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/departamentos?sucursal={branch}");
        forBranch.EnumerateArray().Select(d => d.GetProperty("id").GetInt32()).Should().Contain(id).And.NotContain(twinId);

        var previous = await _api.ScalarAsync("SELECT departamento_id FROM dbo.maquinas WHERE id = @m", ("@m", machine));
        await _api.ExecuteAsync("UPDATE dbo.maquinas SET departamento_id = @d WHERE id = @m", ("@d", id), ("@m", machine));
        try
        {
            var list = (await admin.GetFromJsonAsync<JsonElement>($"/api/v1/sucursales/{branch}/departamentos?limit=100")).GetProperty("data").GetProperty("data");
            list.EnumerateArray().Single(d => d.GetProperty("id").GetInt32() == id).GetProperty("machineCount").GetInt32().Should().Be(1);
            var machines = await admin.GetFromJsonAsync<JsonElement>($"/api/v1/maquinas?departamento={id}");
            machines.GetProperty("data").GetProperty("total").GetInt32().Should().Be(1);
            var refused = await admin.DeleteAsync($"/api/v1/sucursales/{branch}/departamentos/{id}");
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync()).Should().Contain("1 equipo");

            // La base no deja un equipo en un departamento de otra sucursal.
            var crossed = async () => await _api.ExecuteAsync("UPDATE dbo.maquinas SET departamento_id = @d WHERE id = @m", ("@d", twinId), ("@m", machine));
            await crossed.Should().ThrowAsync<Microsoft.Data.SqlClient.SqlException>();
        }
        finally
        {
            await _api.ExecuteAsync("UPDATE dbo.maquinas SET departamento_id = @d WHERE id = @m", ("@d", previous ?? DBNull.Value), ("@m", machine));
        }

        // Configuración los muestra todos, con su sucursal.
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/v1/configuracion/departamentos");
        all.EnumerateArray().Where(d => d.GetProperty("name").GetString() == name).Select(d => d.GetProperty("branchId").GetInt32())
            .Should().BeEquivalentTo([branch, other]);

        (await admin.PutAsJsonAsync($"/api/v1/sucursales/{branch}/departamentos/{id}", new { nombre = name + "B" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/v1/sucursales/{branch}/departamentos/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.DeleteAsync($"/api/v1/sucursales/{other}/departamentos/{twinId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        ((string?)await AuditAsync("departamento.eliminar")).Should().Contain(name);
    }

    [DatabaseFact]
    public async Task Security_settings_are_the_same_ones_remote_access_and_installers_use()
    {
        var admin = await AdminAsync();
        var before = await admin.GetFromJsonAsync<JsonElement>("/api/v1/configuracion/seguridad");
        try
        {
            var saved = await admin.PutAsJsonAsync("/api/v1/configuracion/seguridad", new { pedirPermiso = true, actualizacionAutomatica = false });
            saved.StatusCode.Should().Be(HttpStatusCode.OK);
            var remote = await admin.GetFromJsonAsync<JsonElement>("/api/v1/sesiones-remotas/configuracion");
            remote.GetProperty("pedirPermiso").GetBoolean().Should().BeTrue();
            (await _api.ScalarAsync("SELECT actualizacion_automatica FROM dbo.empresas WHERE nombre = @e", ("@e", Company))).Should().Be(false);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/v1/configuracion/seguridad", new
            {
                pedirPermiso = before.GetProperty("pedirPermiso").GetBoolean(),
                actualizacionAutomatica = before.GetProperty("actualizacionAutomatica").GetBoolean(),
            });
        }
    }

    [DatabaseFact]
    public async Task The_mail_status_never_shows_secrets_and_the_test_mail_is_limited()
    {
        var admin = await AdminAsync();
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/configuracion/correo");
        status.GetProperty("mode").GetString().Should().Be("archivo");
        status.ToString().Should().NotContainAny("Password", "password", "User");

        var first = await admin.PostAsync("/api/v1/configuracion/correo/prueba", null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enviado").GetBoolean().Should().BeFalse("en pruebas se guarda como archivo");
        (await admin.PostAsync("/api/v1/configuracion/correo/prueba", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}

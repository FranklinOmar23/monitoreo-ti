using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class PanelWriteTests(TestDatabase database) : IAsyncLifetime
{
    private const string Caribe = "Grupo Comercial Caribe";
    private const string Rafael = "Clínica San Rafael";
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

    private async Task<int> BranchIdAsync(string company, string name) =>
        (int)(await _api.ScalarAsync(
            "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = @n",
            ("@e", company), ("@n", name)))!;

    private async Task<int> MachineIdAsync(string company, string name) =>
        (int)(await _api.ScalarAsync(
            "SELECT m.id FROM dbo.maquinas m JOIN dbo.empresas e ON e.id = m.empresa_id WHERE e.nombre = @e AND m.nombre = @n",
            ("@e", company), ("@n", name)))!;

    private async Task<object> MachineBody(string name, string branch = "Santo Domingo", string? ip = "10.10.30.50") => new
    {
        nombre = name,
        tipo = "impresora",
        sucursalId = await BranchIdAsync(Caribe, branch),
        marca = "Epson",
        modelo = "L3250",
        ip,
        mac = "02:aa:bb:cc:dd:01",
        fechaCompra = "2025-01-10",
        garantiaHasta = "2027-01-10",
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---------------------------------------------------------------- equipos (W-13)

    [DatabaseFact]
    public async Task An_editor_creates_edits_and_retires_a_machine_with_audit_trail()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));

        var created = await admin.PostAsJsonAsync("/api/v1/maquinas", await MachineBody("PRUEBA-IMP-01"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await JsonAsync(created)).GetProperty("id").GetInt32();

        var updated = await admin.PutAsJsonAsync($"/api/v1/maquinas/{id}", await MachineBody("PRUEBA-IMP-02"));
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.ScalarAsync("SELECT mac FROM dbo.maquinas WHERE id = @id", ("@id", id))).Should().Be("02:AA:BB:CC:DD:01");

        (await admin.PostAsync($"/api/v1/maquinas/{id}/baja", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.ScalarAsync("SELECT estado FROM dbo.maquinas WHERE id = @id", ("@id", id))).Should().Be("baja");

        var history = await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{id}/historial"));
        history.EnumerateArray().Select(h => h.GetProperty("action").GetString())
            .Should().BeEquivalentTo("maquina.crear", "maquina.editar", "maquina.baja");
        history.EnumerateArray().Single(h => h.GetProperty("action").GetString() == "maquina.editar")
            .GetProperty("details").GetString().Should().Contain("PRUEBA-IMP-01").And.Contain("PRUEBA-IMP-02");

        // De baja ya no aparece en la lista ni se puede editar.
        (await admin.PutAsJsonAsync($"/api/v1/maquinas/{id}", await MachineBody("X"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [DatabaseFact]
    public async Task Invalid_data_is_rejected_with_a_clear_message()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));

        var response = await admin.PostAsJsonAsync("/api/v1/maquinas", await MachineBody("PRUEBA-MALA", ip: "999.1.1.1"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("La IP no es válida.");
    }

    [DatabaseFact]
    public async Task Writing_needs_permission_branch_access_and_the_same_company()
    {
        using var employee = _api.ClientWith(await _api.TokenForAsync(Caribe, "Empleado"));
        (await employee.PostAsJsonAsync("/api/v1/maquinas", await MachineBody("PRUEBA-SIN-PERMISO")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var tech = _api.ClientWith(await _api.TokenForAsync(Caribe, "Tecnico", "Santiago"));
        (await tech.PostAsJsonAsync("/api/v1/maquinas", await MachineBody("PRUEBA-OTRA-SUCURSAL", branch: "Santo Domingo")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await tech.PostAsJsonAsync("/api/v1/maquinas", await MachineBody("PRUEBA-SU-SUCURSAL", branch: "Santiago")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Una sucursal de otra empresa no existe para Caribe, aunque se mande su id.
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var foreignBody = new { nombre = "PRUEBA-AJENA", tipo = "computadora", sucursalId = await BranchIdAsync(Rafael, "Santo Domingo") };
        (await admin.PostAsJsonAsync("/api/v1/maquinas", foreignBody)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var foreignMachine = await MachineIdAsync(Rafael, "CONTA-03");
        (await admin.PostAsync($"/api/v1/maquinas/{foreignMachine}/baja", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _api.ScalarAsync("SELECT estado FROM dbo.maquinas WHERE id = @id", ("@id", foreignMachine))).Should().NotBe("baja");
    }

    [DatabaseFact]
    public async Task QR_labels_in_batch_only_include_visible_machines()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var own = await MachineIdAsync(Caribe, "CAJA-01");
        var foreign = await MachineIdAsync(Rafael, "CONTA-03");

        var labels = await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/qr?ids={own},{foreign},abc"));

        labels.GetArrayLength().Should().Be(1);
        labels[0].GetProperty("nombre").GetString().Should().Be("CAJA-01");
        labels[0].GetProperty("png").GetString().Should().StartWith("data:image/png;base64,");
    }

    // ---------------------------------------------------------------- sucursales (W-17)

    [DatabaseFact]
    public async Task Branch_names_are_unique_per_company_and_managers_must_belong_to_it()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));

        (await admin.PostAsJsonAsync("/api/v1/sucursales", new { nombre = "Santiago" })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var foreignUser = (int)(await _api.ScalarAsync("SELECT id FROM dbo.usuarios WHERE correo = 'admin@sanrafael.test'"))!;
        (await admin.PostAsJsonAsync("/api/v1/sucursales", new { nombre = "Prueba Higüey", responsableId = foreignUser }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var created = await admin.PostAsJsonAsync("/api/v1/sucursales", new { nombre = "Prueba Higüey", provincia = "La Altagracia" });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        // San Rafael puede tener su propia "Prueba Higüey": el nombre es único por empresa.
        using var rafael = _api.ClientWith(await _api.TokenForAsync(Rafael, "Administrador"));
        (await rafael.PostAsJsonAsync("/api/v1/sucursales", new { nombre = "Prueba Higüey" })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- instaladores y aprobación (W-18, W-19)

    private async Task<JsonElement> CreateInstallerAsync(HttpClient client, bool requireApproval = false, int? maxUses = null) =>
        await JsonAsync(await client.PostAsJsonAsync("/api/v1/instaladores", new
        {
            sucursalId = await BranchIdAsync(Caribe, "Santiago"),
            nombre = "Lote de cajas",
            diasVigencia = 7,
            usosMax = maxUses,
            requiereAprobacion = requireApproval,
        }));

    [DatabaseFact]
    public async Task An_installer_token_from_the_panel_registers_agents_until_revoked()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));

        var installer = await CreateInstallerAsync(admin);
        var token = installer.GetProperty("token").GetString()!;
        installer.GetProperty("comando").GetString().Should().Contain("/S").And.Contain($"/TOKEN={token}");

        (await _api.RegisterAsync(token, AgentTestApi.NewMac())).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = (await JsonAsync(await admin.GetAsync("/api/v1/instaladores?limit=100"))).GetProperty("data").GetProperty("data");
        var row = list.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == installer.GetProperty("id").GetInt32());
        row.GetProperty("uses").GetInt32().Should().Be(1);
        row.GetProperty("state").GetString().Should().Be("activo");
        list.ToString().Should().NotContain(token, "el token en claro solo se muestra al crearlo");

        (await admin.PostAsync($"/api/v1/instaladores/{installer.GetProperty("id").GetInt32()}/revocar", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.RegisterAsync(token, AgentTestApi.NewMac())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [DatabaseFact]
    public async Task Deleting_a_token_removes_it_but_its_machines_keep_working()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var installer = await CreateInstallerAsync(admin);
        var id = installer.GetProperty("id").GetInt32();
        var register = await JsonAsync(await _api.RegisterAsync(installer.GetProperty("token").GetString()!, AgentTestApi.NewMac()));
        var agent = new RegisteredAgent("", register.GetProperty("credential").GetString()!,
            register.GetProperty("machineId").GetInt32(), register.GetProperty("agentId").GetInt32());

        // Otra empresa no lo puede borrar (ni saber que existe).
        using var rafael = _api.ClientWith(await _api.TokenForAsync(Rafael, "Administrador"));
        (await rafael.DeleteAsync($"/api/v1/instaladores/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await admin.DeleteAsync($"/api/v1/instaladores/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = (await JsonAsync(await admin.GetAsync("/api/v1/instaladores?limit=100"))).GetProperty("data").GetProperty("data");
        list.EnumerateArray().Should().NotContain(t => t.GetProperty("id").GetInt32() == id);
        using var socket = await ConnectAndGreetAsync(_api, agent);
        socket.State.Should().Be(System.Net.WebSockets.WebSocketState.Open, "el equipo ya registrado tiene su propia credencial");
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'instalador.eliminar' AND entidad_id = @id", ("@id", id.ToString())))
            .Should().Be(1);
    }

    [DatabaseFact]
    public async Task Agents_from_an_approval_token_wait_until_someone_approves_them()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var token = (await CreateInstallerAsync(admin, requireApproval: true)).GetProperty("token").GetString()!;

        var register = await JsonAsync(await _api.RegisterAsync(token, AgentTestApi.NewMac(), hostname: "PRUEBA-PENDIENTE"));
        register.GetProperty("aprobado").GetBoolean().Should().BeFalse();
        var agent = new RegisteredAgent("", register.GetProperty("credential").GetString()!, register.GetProperty("machineId").GetInt32(),
            register.GetProperty("agentId").GetInt32());

        var refused = () => ConnectAsync(_api, agent.Credential);
        await refused.Should().ThrowAsync<Exception>("pendiente de aprobación no puede conectarse");

        var pending = (await JsonAsync(await admin.GetAsync("/api/v1/equipos-pendientes?limit=100"))).GetProperty("data").GetProperty("data");
        pending.EnumerateArray().Should().Contain(p => p.GetProperty("agentId").GetInt32() == agent.AgentId);

        (await admin.PostAsync($"/api/v1/equipos-pendientes/{agent.AgentId}/aprobar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var socket = await ConnectAndGreetAsync(_api, agent);
        socket.State.Should().Be(System.Net.WebSockets.WebSocketState.Open);
    }

    [DatabaseFact]
    public async Task Rejecting_a_pending_agent_revokes_it_and_retires_the_machine()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var token = (await CreateInstallerAsync(admin, requireApproval: true)).GetProperty("token").GetString()!;
        var register = await JsonAsync(await _api.RegisterAsync(token, AgentTestApi.NewMac()));
        var agentId = register.GetProperty("agentId").GetInt32();

        (await admin.PostAsync($"/api/v1/equipos-pendientes/{agentId}/rechazar", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _api.ScalarAsync("SELECT revocado FROM dbo.agentes WHERE id = @id", ("@id", agentId))).Should().Be(true);
        (await _api.ScalarAsync("SELECT m.estado FROM dbo.maquinas m JOIN dbo.agentes a ON a.maquina_id = m.id WHERE a.id = @id", ("@id", agentId)))
            .Should().Be("baja");
    }

    [DatabaseFact]
    public async Task Installers_need_their_permission_and_the_download_explains_when_it_is_missing()
    {
        using var tech = _api.ClientWith(await _api.TokenForAsync(Caribe, "Tecnico"));
        (await tech.GetAsync("/api/v1/instaladores")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var download = await admin.GetAsync("/api/v1/instaladores/descarga");
        download.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await download.Content.ReadAsStringAsync()).Should().Contain("instalador_no_disponible");
    }
}

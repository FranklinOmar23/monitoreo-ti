using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Data center (Fase 4.1): salas, gabinetes, dispositivos, cableado y ruta completa.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class NetworkApiTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
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

    private async Task<HttpClient> ClientAsync(string role = "Administrador", params string[] branches) =>
        _api.ClientWith(await _api.TokenForAsync(Company, role, branches));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> CreateAsync(HttpClient client, string path, object body) =>
        (await JsonAsync(await client.PostAsJsonAsync($"/api/v1/red/{path}", body), HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private async Task<int> BranchIdAsync(string branch) => (int)(await _api.ScalarAsync(
        "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = @s",
        ("@e", Company), ("@s", branch)))!;

    private async Task<int> ModelIdAsync(string model) =>
        (int)(await _api.ScalarAsync("SELECT id FROM dbo.modelos_dispositivo WHERE modelo = @m AND empresa_id IS NULL", ("@m", model)))!;

    /// <summary>Sala y gabinete de 42 U en Santiago.</summary>
    private async Task<(int Branch, int Room, int Rack)> RackAsync(HttpClient admin)
    {
        var branch = await BranchIdAsync("Santiago");
        var room = await CreateAsync(admin, "salas", new { nombre = $"Prueba {Guid.NewGuid():N}"[..20], ubicacion = "Piso 1", sucursalId = branch });
        var rack = await CreateAsync(admin, "gabinetes", new { nombre = $"PRUEBA-{Random.Shared.Next(100000)}", salaId = room, alturaU = 42 });
        return (branch, room, rack);
    }

    private static object Device(string name, int model, int branch, int? rack, int? u, object? snmp = null) => new
    {
        nombre = name,
        modeloId = model,
        sucursalId = branch,
        gabineteId = rack,
        posicionU = u,
        ipGestion = (string?)null,
        firmware = (string?)null,
        snmpUsuario = snmp is null ? null : "monitor",
        snmpClaveAutenticacion = snmp is null ? null : "auth-secreta-123",
        snmpClavePrivacidad = snmp is null ? null : "priv-secreta-456",
    };

    [DatabaseFact]
    public async Task The_seed_path_goes_from_the_outlet_to_the_core()
    {
        var admin = await ClientAsync();
        var outletId = (int)(await _api.ScalarAsync("SELECT id FROM dbo.puntos_red WHERE codigo = 'P-2-15'"))!;

        var path = await JsonAsync(await admin.GetAsync($"/api/v1/red/ruta?roseta={outletId}"));
        var names = path.EnumerateArray().Select(h => h.GetProperty("name").GetString()).ToList();
        names[0].Should().Be("P-2-15");
        names[1].Should().Be("PP-A01-01 P12");
        names[2].Should().Be("SW-ACC-02 Gi1/0/12");
        names.Last().Should().StartWith("SW-CORE-01");
    }

    [DatabaseFact]
    public async Task A_device_gets_its_ports_and_the_rack_rejects_overlaps()
    {
        var admin = await ClientAsync();
        var (branch, _, rack) = await RackAsync(admin);
        var model = await ModelIdAsync("Catalyst 9200-48P");

        var sw = await CreateAsync(admin, "dispositivos", Device("PRUEBA-SW-1", model, branch, rack, 40));
        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{sw}"));
        detail.GetProperty("ports").GetArrayLength().Should().Be(52);
        detail.GetProperty("ports")[48].GetProperty("type").GetString().Should().Be("sfp_plus");
        detail.GetProperty("status").GetString().Should().Be("sin_respuesta");

        var overlap = await admin.PostAsJsonAsync("/api/v1/red/dispositivos", Device("PRUEBA-SW-2", model, branch, rack, 40));
        overlap.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await overlap.Content.ReadAsStringAsync()).Should().Contain("PRUEBA-SW-1");

        var tooHigh = await admin.PostAsJsonAsync("/api/v1/red/dispositivos", Device("PRUEBA-PP-1", await ModelIdAsync("CP48BLY"), branch, rack, 42));
        (await tooHigh.Content.ReadAsStringAsync()).Should().Contain("hasta la U 43");

        (await admin.PutAsJsonAsync($"/api/v1/red/dispositivos/{sw}/ubicacion", new { gabineteId = rack, posicionU = 10 })).EnsureSuccessStatusCode();
        var rackDetail = await JsonAsync(await admin.GetAsync($"/api/v1/red/gabinetes/{rack}"));
        rackDetail.GetProperty("devices").EnumerateArray().Single().GetProperty("positionU").GetInt32().Should().Be(10);

        var shrink = await admin.PutAsJsonAsync($"/api/v1/red/gabinetes/{rack}",
            new { nombre = rackDetail.GetProperty("rack").GetProperty("name").GetString(), salaId = rackDetail.GetProperty("rack").GetProperty("roomId").GetInt32(), alturaU = 8 });
        shrink.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.DeleteAsync($"/api/v1/red/gabinetes/{rack}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DatabaseFact]
    public async Task Snmp_keys_are_encrypted_and_never_returned_or_audited()
    {
        var admin = await ClientAsync();
        var (branch, _, rack) = await RackAsync(admin);
        var sw = await CreateAsync(admin, "dispositivos", Device("PRUEBA-SNMP", await ModelIdAsync("Catalyst 9200-48P"), branch, rack, 1, snmp: true));

        var raw = await (await admin.GetAsync($"/api/v1/red/dispositivos/{sw}")).Content.ReadAsStringAsync();
        raw.Should().Contain("\"hasSnmpCredential\":true").And.NotContain("secreta");

        var stored = (byte[])(await _api.ScalarAsync("SELECT snmp_credencial_cifrada FROM dbo.dispositivos_red WHERE id = @id", ("@id", sw)))!;
        Encoding.UTF8.GetString(stored).Should().StartWith("v1:").And.NotContain("secreta");
        var audit = (string)(await _api.ScalarAsync(
            "SELECT TOP 1 detalle FROM dbo.auditoria WHERE entidad = 'dispositivos_red' AND entidad_id = @id", ("@id", sw.ToString())))!;
        audit.Should().NotContain("secreta").And.Contain("\"snmpCredencial\":true");

        // Editar sin mandar las claves las conserva.
        (await admin.PutAsJsonAsync($"/api/v1/red/dispositivos/{sw}", new
        {
            nombre = "PRUEBA-SNMP", modeloId = await ModelIdAsync("Catalyst 9200-48P"), sucursalId = branch, gabineteId = rack, posicionU = 1,
            ipGestion = "10.20.0.5", snmpUsuario = "monitor",
        })).EnsureSuccessStatusCode();
        (await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{sw}"))).GetProperty("hasSnmpCredential").GetBoolean().Should().BeTrue();

        var halfKey = await admin.PostAsJsonAsync("/api/v1/red/dispositivos", new
        {
            nombre = "PRUEBA-SNMP-2", modeloId = await ModelIdAsync("Catalyst 9200-48P"), sucursalId = branch,
            snmpUsuario = "monitor", snmpClaveAutenticacion = "solo-una-clave",
        });
        halfKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task Cables_respect_port_capacity_and_build_the_path()
    {
        var admin = await ClientAsync();
        var (branch, _, rack) = await RackAsync(admin);
        var sw = await CreateAsync(admin, "dispositivos", Device("PRUEBA-ACC", await ModelIdAsync("Catalyst 9200-48P"), branch, rack, 40));
        var pp = await CreateAsync(admin, "dispositivos", Device("PRUEBA-PP", await ModelIdAsync("CP48BLY"), branch, rack, 36));
        var outlet = await CreateAsync(admin, "rosetas", new { codigo = "PRUEBA-1-01", ubicacion = "Caja 1", sucursalId = branch });

        async Task<int> PortAsync(int device, int number) => (int)(await _api.ScalarAsync(
            "SELECT id FROM dbo.puertos WHERE dispositivo_id = @d AND numero = @n", ("@d", device), ("@n", number)))!;
        var ppPort = await PortAsync(pp, 5);
        var swPort = await PortAsync(sw, 5);

        await CreateAsync(admin, "cables", new { puertoAId = ppPort, puntoRedId = outlet, tipoCable = "cat6", etiqueta = "R-0101" });
        await CreateAsync(admin, "cables", new { puertoAId = ppPort, puertoBId = swPort, tipoCable = "cat6", etiqueta = "PC-05" });

        (await admin.PostAsJsonAsync("/api/v1/red/cables", new { puertoAId = ppPort, puertoBId = await PortAsync(sw, 6) }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "el puerto del patch panel ya tiene sus dos cables");
        (await admin.PostAsJsonAsync("/api/v1/red/cables", new { puertoAId = swPort, puertoBId = await PortAsync(pp, 7) }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "un puerto de switch admite un cable");
        (await admin.PostAsJsonAsync("/api/v1/red/cables", new { puertoAId = await PortAsync(pp, 8), puntoRedId = outlet }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "la roseta ya tiene cable");
        (await admin.PostAsJsonAsync("/api/v1/red/cables", new { puertoAId = swPort, puertoBId = swPort }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var path = await JsonAsync(await admin.GetAsync($"/api/v1/red/ruta?roseta={outlet}"));
        path.EnumerateArray().Select(h => h.GetProperty("name").GetString()).Should().Equal("PRUEBA-1-01", "PRUEBA-PP P5", "PRUEBA-ACC 5");

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{pp}"));
        detail.GetProperty("ports")[4].GetProperty("cableTo").GetString().Should().Be("PRUEBA-ACC 5 · Roseta PRUEBA-1-01");
        (await admin.DeleteAsync($"/api/v1/red/rosetas/{outlet}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DatabaseFact]
    public async Task Roles_and_branches_limit_the_data_center()
    {
        var supervisor = await ClientAsync("Supervisor");
        (await supervisor.GetAsync("/api/v1/red/salas")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await supervisor.PostAsJsonAsync("/api/v1/red/salas", new { nombre = "Prueba sala", sucursalId = await BranchIdAsync("Santiago") }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var santiagoOnly = await ClientAsync("Red", "Santiago");
        var rooms = await JsonAsync(await santiagoOnly.GetAsync("/api/v1/red/salas"));
        rooms.EnumerateArray().Should().NotContain(r => r.GetProperty("name").GetString() == "Data Center Principal");
        var core = (int)(await _api.ScalarAsync(
            "SELECT d.id FROM dbo.dispositivos_red d JOIN dbo.empresas e ON e.id = d.empresa_id WHERE e.nombre = @e AND d.nombre = 'SW-CORE-01'", ("@e", Company)))!;
        (await santiagoOnly.GetAsync($"/api/v1/red/dispositivos/{core}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await santiagoOnly.PostAsJsonAsync("/api/v1/red/salas", new { nombre = "Prueba ajena", sucursalId = await BranchIdAsync("Santo Domingo") }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var admin = await ClientAsync();
        var baseModel = await ModelIdAsync("CP48BLY");
        (await admin.PutAsJsonAsync($"/api/v1/red/modelos/{baseModel}", new { marca = "Panduit", modelo = "CP48BLY", tipo = "patch_panel", alturaU = 2, cantidadPuertos = 48, esActivo = false }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "el catálogo base es de solo lectura");
        await CreateAsync(admin, "modelos", new { marca = "Prueba", modelo = "SW-24", tipo = "switch", alturaU = 1, cantidadPuertos = 26, tipoPuertos = "24 RJ45 + 2 SFP", esActivo = true });
    }
}

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
            nombre = "PRUEBA-SNMP",
            modeloId = await ModelIdAsync("Catalyst 9200-48P"),
            sucursalId = branch,
            gabineteId = rack,
            posicionU = 1,
            ipGestion = "10.20.0.5",
            snmpUsuario = "monitor",
        })).EnsureSuccessStatusCode();
        (await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{sw}"))).GetProperty("hasSnmpCredential").GetBoolean().Should().BeTrue();

        var halfKey = await admin.PostAsJsonAsync("/api/v1/red/dispositivos", new
        {
            nombre = "PRUEBA-SNMP-2",
            modeloId = await ModelIdAsync("Catalyst 9200-48P"),
            sucursalId = branch,
            snmpUsuario = "monitor",
            snmpClaveAutenticacion = "solo-una-clave",
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

    [DatabaseFact]
    public async Task Changing_the_ports_of_a_model_in_use_adjusts_its_devices()
    {
        var admin = await ClientAsync();
        var (branch, _, rack) = await RackAsync(admin);
        var name = $"FG-{Random.Shared.Next(100000)}";
        object Model(int ports, string description) =>
            new { marca = "Prueba", modelo = name, tipo = "firewall", alturaU = 1, cantidadPuertos = ports, tipoPuertos = description, esActivo = true };
        var model = await CreateAsync(admin, "modelos", Model(10, "10 RJ45"));
        var device = await CreateAsync(admin, "dispositivos", Device("PRUEBA-FGT", model, branch, rack, 30));

        // Más puertos: se agregan al dispositivo con su tipo.
        await JsonAsync(await admin.PutAsJsonAsync($"/api/v1/red/modelos/{model}", Model(30, "22 RJ45 + 8 SFP+")));
        var ports = (await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{device}"))).GetProperty("ports").EnumerateArray().ToList();
        ports.Should().HaveCount(30);
        ports.Single(p => p.GetProperty("number").GetInt32() == 30).GetProperty("type").GetString().Should().Be("sfp_plus");

        // Menos puertos: se quitan los libres; uno con cable lo impide.
        var outlet = await CreateAsync(admin, "rosetas", new { codigo = $"PRUEBA-{Random.Shared.Next(100000)}", sucursalId = branch });
        var port28 = ports.Single(p => p.GetProperty("number").GetInt32() == 28).GetProperty("id").GetInt32();
        await CreateAsync(admin, "cables", new { puertoAId = port28, puntoRedId = outlet });
        (await admin.PutAsJsonAsync($"/api/v1/red/modelos/{model}", Model(24, "24 RJ45"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await JsonAsync(await admin.PutAsJsonAsync($"/api/v1/red/modelos/{model}", Model(28, "22 RJ45 + 6 SFP+")));
        (await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{device}"))).GetProperty("ports").GetArrayLength().Should().Be(28);
    }

    private static async Task<JsonElement> ImportAsync(HttpClient client, string type, string csv, bool apply) =>
        await JsonAsync(await client.PostAsJsonAsync(apply ? "/api/v1/red/importar" : "/api/v1/red/importar/vista-previa", new { tipo = type, contenido = csv }));

    private static string[] RowErrors(JsonElement result, int index) =>
        result.GetProperty("rows")[index].GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();

    private async Task<int> CountAsync(string sql) => (int)(await _api.ScalarAsync(sql))!;

    [DatabaseFact]
    public async Task The_import_previews_without_saving_and_saves_all_or_nothing()
    {
        var admin = await ClientAsync();
        var (_, _, rack) = await RackAsync(admin);
        var rackInfo = (await JsonAsync(await admin.GetAsync($"/api/v1/red/gabinetes/{rack}"))).GetProperty("rack");
        var rackName = rackInfo.GetProperty("name").GetString();
        var roomName = rackInfo.GetProperty("roomName").GetString();
        const string countOutlets = "SELECT COUNT(*) FROM dbo.puntos_red WHERE codigo LIKE 'PRUEBA-%'";
        var outlets = await CountAsync(countOutlets);

        // Rosetas con el separador de Excel en español; la segunda fila repite el código de la primera.
        const string rosetas = "Código;Ubicación;Sucursal\nPRUEBA-R1;Contabilidad;Santiago\nPRUEBA-R1;Caja;Santiago\n";
        var preview = await ImportAsync(admin, "rosetas", rosetas, apply: false);
        preview.GetProperty("valid").GetInt32().Should().Be(1);
        preview.GetProperty("applied").GetBoolean().Should().BeFalse();
        RowErrors(preview, 1).Should().ContainSingle().Which.Should().Contain("Ya hay una roseta");
        (await CountAsync(countOutlets)).Should().Be(outlets, "la vista previa no guarda");

        (await ImportAsync(admin, "rosetas", rosetas, apply: true)).GetProperty("applied").GetBoolean().Should().BeFalse();
        (await CountAsync(countOutlets)).Should().Be(outlets, "con una fila mala no se guarda ninguna");

        (await ImportAsync(admin, "rosetas", "codigo,ubicacion,sucursal\nPRUEBA-R1,Contabilidad,Santiago\n", apply: true))
            .GetProperty("applied").GetBoolean().Should().BeTrue();

        // Dispositivos: dos en la misma U chocan entre sí aunque ninguno exista todavía.
        var devices =
            "nombre,marca,modelo,sucursal,sala,gabinete,posicion_u,ip_gestion\n"
            + $"PRUEBA-IMP-SW,Cisco,Catalyst 9200-48P,Santiago,{roomName},{rackName},30,10.30.0.2\n"
            + $"PRUEBA-IMP-PP,Panduit,CP48BLY,Santiago,,{rackName},30,\n";
        preview = await ImportAsync(admin, "dispositivos", devices + "PRUEBA-IMP-X,Cisco,No existe,Santiago,,,,\n", apply: false);
        preview.GetProperty("valid").GetInt32().Should().Be(1);
        RowErrors(preview, 1).Single().Should().Contain("PRUEBA-IMP-SW");
        RowErrors(preview, 2).Single().Should().Contain("no está en el catálogo");

        (await ImportAsync(admin, "dispositivos", devices.Replace($"{rackName},30,\n", $"{rackName},25,\n"), apply: true))
            .GetProperty("applied").GetBoolean().Should().BeTrue();

        // Cables: el puerto se busca por su nombre ("P5") o por su número ("5").
        const string cables = "dispositivo_a,puerto_a,dispositivo_b,puerto_b,roseta,tipo_cable,etiqueta\n"
            + "PRUEBA-IMP-PP,5,,,PRUEBA-R1,CAT6,R1\n"
            + "PRUEBA-IMP-PP,P5,PRUEBA-IMP-SW,5,,cat6,PP5-SW5\n";
        preview = await ImportAsync(admin, "cables", cables + "PRUEBA-IMP-SW,Gi9/9/9,,,PRUEBA-R1,,\n", apply: false);
        RowErrors(preview, 2).Single().Should().Contain("no tiene el puerto Gi9/9/9");
        (await ImportAsync(admin, "cables", cables, apply: true)).GetProperty("applied").GetBoolean().Should().BeTrue();

        var outletId = await CountAsync("SELECT id FROM dbo.puntos_red WHERE codigo = 'PRUEBA-R1'");
        var path = await JsonAsync(await admin.GetAsync($"/api/v1/red/ruta?roseta={outletId}"));
        path.EnumerateArray().Select(h => h.GetProperty("name").GetString()).Should().Contain("PRUEBA-IMP-SW 5");

        var missingColumns = await ImportAsync(admin, "cables", "dispositivo,puerto\nX,1\n", apply: false);
        missingColumns.GetProperty("fileErrors")[0].GetString().Should().Contain("dispositivo_a");
    }

    [DatabaseFact]
    public async Task The_import_only_reaches_the_users_branches()
    {
        var red = await ClientAsync("Red", "Santo Domingo");
        var result = await ImportAsync(red, "rosetas", "codigo,sucursal\nPRUEBA-R9,Santiago\n", apply: false);
        RowErrors(result, 0).Single().Should().Contain("no tiene acceso");
    }

    [DatabaseFact]
    public async Task The_history_lists_changes_with_names()
    {
        var admin = await ClientAsync();
        var (branch, _, rack) = await RackAsync(admin);
        var sw = await CreateAsync(admin, "dispositivos", Device("PRUEBA-HIST", await ModelIdAsync("Catalyst 9200-48P"), branch, rack, 5));
        (await admin.PutAsJsonAsync($"/api/v1/red/dispositivos/{sw}/ubicacion", new { gabineteId = rack, posicionU = 12 })).EnsureSuccessStatusCode();
        var port = (int)(await _api.ScalarAsync("SELECT id FROM dbo.puertos WHERE dispositivo_id = @d AND numero = 3", ("@d", sw)))!;
        (await admin.PutAsJsonAsync($"/api/v1/red/puertos/{port}", new { nombre = "Gi1/0/3", tipo = "rj45", vlan = 30, esHostVm = false })).EnsureSuccessStatusCode();

        var history = await JsonAsync(await admin.GetAsync($"/api/v1/red/historial?entidad=dispositivos_red&id={sw}"));
        var items = history.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("title").GetString()).Should().Equal("Puerto editado", "Dispositivo movido", "Dispositivo registrado");
        items[0].GetProperty("subject").GetString().Should().Be("PRUEBA-HIST Gi1/0/3");
        var moved = items[1].GetProperty("changes").EnumerateArray().Single();
        moved.GetProperty("field").GetString().Should().Be("U");
        moved.GetProperty("before").GetString().Should().Be("5");
        moved.GetProperty("after").GetString().Should().Be("12");
        items[2].GetProperty("subject").GetString().Should().Be("PRUEBA-HIST");

        // Quien solo ve Santo Domingo no ve lo de Santiago.
        var other = await ClientAsync("Red", "Santo Domingo");
        var hidden = await JsonAsync(await other.GetAsync($"/api/v1/red/historial?entidad=dispositivos_red&id={sw}"));
        hidden.GetProperty("total").GetInt32().Should().Be(0);
    }
}

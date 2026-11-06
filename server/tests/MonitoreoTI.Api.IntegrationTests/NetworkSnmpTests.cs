using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Infrastructure.Health;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// Recolector SNMP y correlación (Fase 4.2 y 4.3): el agente recibe sus dispositivos con las claves selladas, reporta
/// los sondeos y el servidor ubica cada PC en su switch y puerto; desconectar el cable pone el puerto en rojo.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NetworkSnmpTests(TestDatabase database) : IAsyncLifetime
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> CreateAsync(HttpClient client, string path, object body) =>
        (await JsonAsync(await client.PostAsJsonAsync($"/api/v1/red/{path}", body), HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private async Task<int> SantiagoAsync() => (int)(await _api.ScalarAsync(
        "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = 'Santiago'", ("@e", Company)))!;

    private async Task<int> ModelAsync() =>
        (int)(await _api.ScalarAsync("SELECT id FROM dbo.modelos_dispositivo WHERE modelo = 'Catalyst 9200-48P' AND empresa_id IS NULL"))!;

    private static object Iface(int index, string name, string oper = "up", long errors = 0) => new
    {
        indice = index,
        tipo = 6,
        nombre = name,
        admin = "up",
        operativo = oper,
        velocidadMbps = 1000,
        entradaBytes = 1000,
        salidaBytes = 2000,
        erroresEntrada = errors,
        erroresSalida = 0,
        descartes = 0,
    };

    private sealed record Lab(HttpClient Admin, RegisteredAgent Collector, RegisteredAgent Pc, int Switch, int Core, System.Net.WebSockets.WebSocket Socket);

    /// <summary>Un recolector conectado, una PC con agente (su tarjeta registrada), un switch consultado y un core.</summary>
    private async Task<Lab> LabAsync()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var branch = await SantiagoAsync();
        var room = await CreateAsync(admin, "salas", new { nombre = $"Prueba {Guid.NewGuid():N}"[..20], sucursalId = branch });
        var rack = await CreateAsync(admin, "gabinetes", new { nombre = $"PRUEBA-{Random.Shared.Next(100000)}", salaId = room, alturaU = 42 });

        var collector = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var pc = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using (var pcSocket = await ConnectAndGreetAsync(_api, pc))
            await SendAckedAsync(pcSocket, "interfaces_red", new { interfaces = new[] { new { nombre = "Ethernet", mac = pc.Mac, ip = "10.30.20.31", tipo = "cableada" } } });

        var suffix = Random.Shared.Next(100000);
        var core = await CreateAsync(admin, "dispositivos", new { nombre = $"PRUEBA-CORE-{suffix}", modeloId = await ModelAsync(), sucursalId = branch, gabineteId = rack, posicionU = 40 });
        var sw = await CreateAsync(admin, "dispositivos", new
        {
            nombre = $"PRUEBA-SW-{suffix}",
            modeloId = await ModelAsync(),
            sucursalId = branch,
            gabineteId = rack,
            posicionU = 38,
            ipGestion = "10.30.0.9",
            snmpUsuario = "monitor",
            snmpClaveAutenticacion = "auth-secreta-123",
            snmpClavePrivacidad = "priv-secreta-456",
            recolectorId = collector.AgentId,
        });

        var socket = await ConnectAsync(_api, collector.Credential);
        await SendAsync(socket, "hola", new { version = "0.1.0", hostname = "RECOLECTOR", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        return new Lab(admin, collector, pc, sw, core, socket);
    }

    private async Task<string> CoreNameAsync(Lab lab) =>
        (await JsonAsync(await lab.Admin.GetAsync($"/api/v1/red/dispositivos/{lab.Core}"))).GetProperty("name").GetString()!;

    private async Task<JsonElement> DeviceAsync(Lab lab) => await JsonAsync(await lab.Admin.GetAsync($"/api/v1/red/dispositivos/{lab.Switch}"));

    private object Poll(Lab lab, string coreName, string pcPort = "up") => new
    {
        dispositivoId = lab.Switch,
        ok = true,
        sistema = new { descripcion = "Cisco IOS Software, C9200, Version 17.9.4, RELEASE", nombre = "SW", uptimeSegundos = 1000 },
        interfaces = new[] { Iface(10101, "Gi1/0/1", pcPort), Iface(10102, "Gi1/0/2"), Iface(10103, "Gi1/0/3", "down"), Iface(10149, "Te1/1/1") },
        mac = new object[]
        {
            new { ifIndex = 10101, mac = lab.Pc.Mac, vlan = 20 },
            new { ifIndex = 10102, mac = "3C:52:82:00:00:99", vlan = 20 },
            new { ifIndex = 10102, mac = "DA:A1:19:00:00:01", vlan = 20 },
            new { ifIndex = 10102, mac = "DE:A1:19:00:00:02", vlan = 20 },
        }.Concat(Enumerable.Range(1, 12).Select(i => (object)new { ifIndex = 10149, mac = $"00:11:22:33:44:{i:X2}", vlan = 1 })).ToArray(),
        arp = new[] { new { mac = "3C:52:82:00:00:99", ip = "10.30.20.50" } },
        lldp = new[] { new { puertoLocal = 10149, sistema = $"{coreName}.caribe.local", puerto = "1" } },
    };

    private async Task EvaluateNetworkAsync() =>
        await _api.Factory.Services.GetRequiredService<NetworkAlertEngine>().EvaluateCompanyAsync(await database.GetCompanyIdAsync(Company), CancellationToken.None);

    [DatabaseFact]
    public async Task The_collector_gets_its_devices_and_a_poll_places_the_pc_on_its_port()
    {
        var lab = await LabAsync();
        using var socket = lab.Socket;

        // Bienvenida: es recolector y recibe el switch con las claves selladas para él.
        var welcome = (await ReceiveAsync(socket))!.Value.GetProperty("datos");
        welcome.GetProperty("es_recolector").GetBoolean().Should().BeTrue();
        var assigned = welcome.GetProperty("recolector").GetProperty("dispositivos").EnumerateArray().Single();
        assigned.GetProperty("ip").GetString().Should().Be("10.30.0.9");
        welcome.GetRawText().Should().NotContain("secreta");
        SnmpCredentialSeal.Open(lab.Collector.Credential, assigned.GetProperty("credencial").GetString()!).Should().Contain("auth-secreta-123");

        var coreName = await CoreNameAsync(lab);
        await SendAckedAsync(socket, "snmp_resultado", Poll(lab, coreName), DateTimeOffset.UtcNow.AddMinutes(-3));

        var device = await DeviceAsync(lab);
        device.GetProperty("status").GetString().Should().Be("en_linea");
        device.GetProperty("firmware").GetString().Should().Be("17.9.4");
        device.GetProperty("collectorId").GetInt32().Should().Be(lab.Collector.AgentId);
        var ports = device.GetProperty("ports").EnumerateArray().ToList();
        ports[0].GetProperty("name").GetString().Should().Be("Gi1/0/1");
        ports[0].GetProperty("status").GetString().Should().Be("conectado");
        ports[0].GetProperty("machineId").GetInt32().Should().Be(lab.Pc.MachineId);
        ports[2].GetProperty("status").GetString().Should().Be("libre");
        ports[3].GetProperty("isTrunk").GetBoolean().Should().BeTrue("LLDP ve el core y tiene más de 10 MAC");

        var location = await JsonAsync(await lab.Admin.GetAsync($"/api/v1/maquinas/{lab.Pc.MachineId}/conexion-red"));
        location.GetProperty("connection").GetProperty("deviceId").GetInt32().Should().Be(lab.Switch);
        location.GetProperty("connection").GetProperty("portLabel").GetString().Should().Be("Gi1/0/1");
        location.GetProperty("connection").GetProperty("vlan").GetInt32().Should().Be(20);

        var unknown = ((await JsonAsync(await lab.Admin.GetAsync("/api/v1/red/desconocidos?limit=100"))).GetProperty("data").GetProperty("data")).EnumerateArray()
            .Where(u => u.GetProperty("deviceId").GetInt32() == lab.Switch).ToList();
        unknown.Single(u => !u.GetProperty("isRandom").GetBoolean()).GetProperty("ip").GetString().Should().Be("10.30.20.50");
        unknown.Single(u => u.GetProperty("isRandom").GetBoolean()).GetProperty("count").GetInt32().Should().Be(2);
        unknown.Should().NotContain(u => u.GetProperty("mac").GetString()!.StartsWith("00:11:22"), "las MAC de una troncal no ubican equipos");

        var differences = ((await JsonAsync(await lab.Admin.GetAsync("/api/v1/red/diferencias?limit=100"))).GetProperty("data").GetProperty("data")).EnumerateArray()
            .Where(d => d.GetProperty("deviceId").GetInt32() == lab.Switch).Select(d => d.GetProperty("kind").GetString()).ToList();
        differences.Should().Contain("equipo_sin_cable").And.Contain("enlace_sin_cable");

        var topology = await JsonAsync(await lab.Admin.GetAsync("/api/v1/red/topologia"));
        topology.GetProperty("links").EnumerateArray().Should().Contain(l =>
            l.GetProperty("kind").GetString() == "lldp"
            && new[] { l.GetProperty("sourceId").GetInt32(), l.GetProperty("targetId").GetInt32() }.Order().SequenceEqual(new[] { lab.Switch, lab.Core }.Order()));

        // Equipos finales por puerto (decisión 74): la PC con agente y la MAC desconocida; ni las aleatorias ni las de la troncal.
        var endpoints = topology.GetProperty("endpoints").EnumerateArray().Where(e => e.GetProperty("deviceId").GetInt32() == lab.Switch).ToList();
        endpoints.Should().HaveCount(2);
        var pcEndpoint = endpoints.Single(e => e.GetProperty("machineId").ValueKind == JsonValueKind.Number);
        pcEndpoint.GetProperty("machineId").GetInt32().Should().Be(lab.Pc.MachineId);
        pcEndpoint.GetProperty("portLabel").GetString().Should().Be("Gi1/0/1");
        pcEndpoint.GetProperty("vlan").GetInt32().Should().Be(20);
        pcEndpoint.GetProperty("category").GetString().Should().Be("pc");
        pcEndpoint.GetProperty("hasAgent").GetBoolean().Should().BeTrue();
        pcEndpoint.GetProperty("kind").GetString().Should().Be("detectado");
        var other = endpoints.Single(e => e.GetProperty("machineId").ValueKind == JsonValueKind.Null);
        other.GetProperty("ip").GetString().Should().Be("10.30.20.50");
        other.GetProperty("category").GetString().Should().Be("desconocido");
        topology.GetProperty("macTableReadAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        var found = await JsonAsync(await lab.Admin.GetAsync($"/api/v1/red/buscar?q={lab.Pc.Mac}"));
        found.EnumerateArray().First().GetProperty("portLabel").GetString().Should().EndWith("Gi1/0/1");
    }

    [DatabaseFact]
    public async Task Unplugging_the_pc_turns_the_port_red_and_opens_an_alert_that_resolves_itself()
    {
        var lab = await LabAsync();
        using var socket = lab.Socket;
        await ReceiveAsync(socket); // bienvenida
        var coreName = await CoreNameAsync(lab);

        await SendAckedAsync(socket, "snmp_resultado", Poll(lab, coreName), DateTimeOffset.UtcNow.AddMinutes(-3));
        await SendAckedAsync(socket, "snmp_resultado", Poll(lab, coreName, pcPort: "down"), DateTimeOffset.UtcNow.AddMinutes(-2));

        var port = (await DeviceAsync(lab)).GetProperty("ports")[0];
        port.GetProperty("status").GetString().Should().Be("caido");
        await EvaluateNetworkAsync();

        var alerts = await JsonAsync(await lab.Admin.GetAsync("/api/v1/alertas?red=true"));
        var alert = alerts.GetProperty("data").GetProperty("data").EnumerateArray()
            .Single(a => a.GetProperty("deviceId").GetInt32() == lab.Switch);
        alert.GetProperty("level").GetString().Should().Be("critico");
        alert.GetProperty("machineId").GetInt32().Should().Be(lab.Pc.MachineId);
        alert.GetProperty("deviceName").GetString().Should().StartWith("PRUEBA-SW-");
        alert.GetProperty("branchName").GetString().Should().Be("Santiago");
        (await DeviceAsync(lab)).GetProperty("status").GetString().Should().Be("critico");

        await SendAckedAsync(socket, "snmp_resultado", Poll(lab, coreName), DateTimeOffset.UtcNow.AddMinutes(-1));
        await EvaluateNetworkAsync();
        var after = await JsonAsync(await lab.Admin.GetAsync($"/api/v1/alertas/{alert.GetProperty("id").GetInt64()}"));
        after.GetProperty("status").GetString().Should().Be("resuelta");
        (await DeviceAsync(lab)).GetProperty("status").GetString().Should().Be("en_linea");
    }

    [DatabaseFact]
    public async Task Poll_now_reaches_the_collector_and_older_agents_get_their_list_again()
    {
        var lab = await LabAsync();
        using var socket = lab.Socket;
        await ReceiveAsync(socket); // bienvenida

        // El recolector de la prueba saludó con la versión 0.1.0: no conoce "sondear_ahora" y recibe su lista otra vez.
        (await lab.Admin.PostAsync($"/api/v1/red/dispositivos/{lab.Switch}/sondear", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var resent = (await ReceiveAsync(socket))!.Value;
        resent.GetProperty("tipo").GetString().Should().Be("config_actualizada");

        // Uno actualizado recibe el pedido de ese dispositivo.
        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.3.4' WHERE id = @id", ("@id", lab.Collector.AgentId));
        (await lab.Admin.PostAsync($"/api/v1/red/dispositivos/{lab.Switch}/sondear", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var request = (await ReceiveAsync(socket))!.Value;
        request.GetProperty("tipo").GetString().Should().Be("sondear_ahora");
        request.GetProperty("datos").GetProperty("dispositivo_id").GetInt32().Should().Be(lab.Switch);

        // Sin datos SNMP no hay qué sondear; con el recolector desconectado, se avisa.
        (await lab.Admin.PostAsync($"/api/v1/red/dispositivos/{lab.Core}/sondear", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "fin", CancellationToken.None);
        await Task.Delay(300);
        (await lab.Admin.PostAsync($"/api/v1/red/dispositivos/{lab.Switch}/sondear", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [DatabaseFact]
    public async Task Failures_are_explained_and_only_the_assigned_collector_counts()
    {
        var lab = await LabAsync();
        using var socket = lab.Socket;
        await ReceiveAsync(socket); // bienvenida

        await SendAckedAsync(socket, "snmp_resultado", new { dispositivoId = lab.Switch, ok = false, error = "SNMP rechazó la consulta: usuario desconocido" });
        var device = await DeviceAsync(lab);
        device.GetProperty("status").GetString().Should().Be("sin_respuesta");
        device.GetProperty("lastError").GetString().Should().Contain("usuario desconocido");

        // Otro agente no puede reportar un dispositivo que no tiene asignado.
        using (var other = await ConnectAndGreetAsync(_api, lab.Pc))
            await SendAckedAsync(other, "snmp_resultado", Poll(lab, await CoreNameAsync(lab)));
        (await DeviceAsync(lab)).GetProperty("lastPolledAt").ValueKind.Should().Be(JsonValueKind.Null);

        // Cambiar el dispositivo avisa al recolector conectado.
        var branch = await SantiagoAsync();
        (await lab.Admin.PutAsJsonAsync($"/api/v1/red/dispositivos/{lab.Switch}", new
        {
            nombre = device.GetProperty("name").GetString(),
            modeloId = await ModelAsync(),
            sucursalId = branch,
            gabineteId = device.GetProperty("rackId").GetInt32(),
            posicionU = 38,
            ipGestion = "10.30.0.10",
            snmpUsuario = "monitor",
            recolectorId = lab.Collector.AgentId,
        })).EnsureSuccessStatusCode();
        var update = (await ReceiveAsync(socket))!.Value;
        update.GetProperty("tipo").GetString().Should().Be("config_actualizada");
        update.GetProperty("datos").GetProperty("recolector").GetProperty("dispositivos")[0].GetProperty("ip").GetString().Should().Be("10.30.0.10");

        // Un recolector de otra sucursal no se acepta.
        var foreign = await _api.RegisterNewAgentAsync(Company, "Santo Domingo");
        var rejected = await lab.Admin.PutAsJsonAsync($"/api/v1/red/dispositivos/{lab.Switch}", new
        {
            nombre = device.GetProperty("name").GetString(),
            modeloId = await ModelAsync(),
            sucursalId = branch,
            gabineteId = device.GetProperty("rackId").GetInt32(),
            posicionU = 38,
            snmpUsuario = "monitor",
            recolectorId = foreign.AgentId,
        });
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task A_port_can_be_assigned_by_hand()
    {
        var lab = await LabAsync();
        using var socket = lab.Socket;
        var port = (await DeviceAsync(lab)).GetProperty("ports")[5].GetProperty("id").GetInt32();

        (await lab.Admin.PutAsJsonAsync($"/api/v1/red/puertos/{port}/asignacion", new { maquinaId = lab.Pc.MachineId })).EnsureSuccessStatusCode();
        var assigned = (await DeviceAsync(lab)).GetProperty("ports")[5];
        assigned.GetProperty("isManualAssignment").GetBoolean().Should().BeTrue();
        assigned.GetProperty("machineId").GetInt32().Should().Be(lab.Pc.MachineId);
        var location = await JsonAsync(await lab.Admin.GetAsync($"/api/v1/maquinas/{lab.Pc.MachineId}/conexion-red"));
        location.GetProperty("connection").GetProperty("isManual").GetBoolean().Should().BeTrue();

        (await lab.Admin.PutAsJsonAsync($"/api/v1/red/puertos/{port}/asignacion", new { maquinaId = (int?)null, troncal = (bool?)null })).EnsureSuccessStatusCode();
        (await DeviceAsync(lab)).GetProperty("ports")[5].GetProperty("isManualAssignment").GetBoolean().Should().BeFalse();
    }
}

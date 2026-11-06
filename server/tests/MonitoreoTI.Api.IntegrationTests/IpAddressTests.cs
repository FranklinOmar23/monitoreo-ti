using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// Direcciones IP (decisión 72): una subred con su recolector; "Escanear ahora" le pide el barrido, el recolector devuelve
/// los pings y la ARP, y cada dirección queda en uso (conocida o sin identificar), reservada o libre.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IpAddressTests(TestDatabase database) : IAsyncLifetime
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

    private async Task<int> SantiagoAsync() => (int)(await _api.ScalarAsync(
        "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = 'Santiago'", ("@e", Company)))!;

    /// <summary>Una red /24 que ninguna otra prueba usa ("10.X.Y").</summary>
    private static string NewNetwork() => $"10.{Random.Shared.Next(100, 250)}.{Random.Shared.Next(0, 255)}";

    private static JsonElement Address(JsonElement detail, string ip) =>
        detail.GetProperty("addresses").EnumerateArray().Single(a => a.GetProperty("ip").GetString() == ip);

    private static async Task<JsonElement> NextOfTypeAsync(System.Net.WebSockets.WebSocket socket, string tipo)
    {
        for (var i = 0; i < 10; i++)
        {
            var message = await ReceiveAsync(socket) ?? throw new InvalidOperationException("El servidor cerró la conexión.");
            if (message.GetProperty("tipo").GetString() == tipo)
                return message.GetProperty("datos");
        }
        throw new InvalidOperationException($"No llegó {tipo}.");
    }

    [DatabaseFact]
    public async Task A_sweep_marks_each_address_in_use_reserved_or_free()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var net = NewNetwork();
        var collector = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var pc = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var printerMac = AgentTestApi.NewMac();

        var subnetId = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/red/subredes", new
        {
            nombre = "Datos Santiago",
            sucursalId = await SantiagoAsync(),
            red = $"{net}.77/24",
            vlan = 10,
            puertaEnlace = $"{net}.1",
            recolectorId = collector.AgentId,
            barridoHoras = 0,
            diasLibre = 30,
        }), HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await JsonAsync(await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/reservas",
            new { desde = $"{net}.100", hasta = $"{net}.120", motivo = "DHCP" }), HttpStatusCode.Created);

        // La PC reporta su IP fija; no responde al ping (firewall de Windows) ni aparece en la ARP.
        using (var pcSocket = await ConnectAndGreetAsync(_api, pc))
            await SendAckedAsync(pcSocket, "interfaces_red", new { interfaces = new[] { new { nombre = "Ethernet", mac = pc.Mac, ip = $"{net}.31", tipo = "cableada" } } });

        // Un recolector con un agente anterior a 0.3.4 no sabe barrer: se explica en vez de quedar esperando.
        using (var old = await ConnectAndGreetAsync(_api, collector))
        {
            (await admin.PostAsync($"/api/v1/red/subredes/{subnetId}/escanear", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}"))).GetProperty("subnet").GetProperty("collectorCanSweep").GetBoolean().Should().BeFalse();
        }
        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.3.4' WHERE id = @id", ("@id", collector.AgentId));
        await Task.Delay(300);

        // Sin el recolector conectado no se puede escanear.
        (await admin.PostAsync($"/api/v1/red/subredes/{subnetId}/escanear", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var socket = await ConnectAsync(_api, collector.Credential);
        await SendAsync(socket, "hola", new { version = "0.3.4", hostname = "RECOLECTOR", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        await ReceiveAsync(socket); // bienvenida
        await JsonAsync(await admin.PostAsync($"/api/v1/red/subredes/{subnetId}/escanear", null));
        var request = await NextOfTypeAsync(socket, "barrido_ip");
        request.GetProperty("subred_id").GetInt32().Should().Be(subnetId);
        var ips = request.GetProperty("ips").EnumerateArray().Select(i => i.GetString()).ToList();
        ips.Should().HaveCount(254).And.Contain($"{net}.1").And.Contain($"{net}.254").And.NotContain($"{net}.0");

        // Mientras tanto, el panel ve el barrido en curso.
        var pending = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}"));
        pending.GetProperty("subnet").GetProperty("sweepRequestedAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        await SendAckedAsync(socket, "barrido_ip_resultado", new
        {
            subredId = subnetId,
            ok = true,
            responden = new[] { $"{net}.1", $"{net}.60", "192.0.2.10" },
            arp = new[]
            {
                new { ip = $"{net}.60", mac = "3C:52:82:00:00:99" },
                new { ip = $"{net}.70", mac = printerMac },
                new { ip = $"{net}.255", mac = "FF:FF:FF:FF:FF:FF" },
            },
            // Nombres: el de una IP vista se guarda; el de una que nadie respondió (registro viejo del DNS) no.
            nombres = new[]
            {
                new { ip = $"{net}.60", nombre = "IMP-RECEPCION", origen = "netbios" },
                new { ip = $"{net}.200", nombre = "FANTASMA", origen = "dns" },
            },
        });

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}"));
        var subnet = detail.GetProperty("subnet");
        subnet.GetProperty("cidr").GetString().Should().Be($"{net}.0/24");
        subnet.GetProperty("lastSweepAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        subnet.GetProperty("sweepRequestedAt").ValueKind.Should().Be(JsonValueKind.Null);
        subnet.GetProperty("total").GetInt32().Should().Be(254);
        // .1 (puerta de enlace) y .100 a .120 (DHCP).
        subnet.GetProperty("reserved").GetInt32().Should().Be(22);

        Address(detail, $"{net}.1").GetProperty("state").GetString().Should().Be("reservada");
        Address(detail, $"{net}.1").GetProperty("isGateway").GetBoolean().Should().BeTrue();

        var known = Address(detail, $"{net}.31");
        known.GetProperty("state").GetString().Should().Be("en_uso");
        known.GetProperty("machineId").GetInt32().Should().Be(pc.MachineId);
        known.GetProperty("machineSource").GetString().Should().Be("agente");

        var unknown = Address(detail, $"{net}.60");
        unknown.GetProperty("state").GetString().Should().Be("sin_identificar");
        unknown.GetProperty("mac").GetString().Should().Be("3C:52:82:00:00:99");
        unknown.GetProperty("lastPingAt").ValueKind.Should().NotBe(JsonValueKind.Null);
        unknown.GetProperty("hostname").GetString().Should().Be("IMP-RECEPCION");
        unknown.GetProperty("hostnameSource").GetString().Should().Be("netbios");
        Address(detail, $"{net}.200").GetProperty("state").GetString().Should().Be("libre");
        Address(detail, $"{net}.200").GetProperty("hostname").ValueKind.Should().Be(JsonValueKind.Null);

        Address(detail, $"{net}.70").GetProperty("state").GetString().Should().Be("sin_identificar");
        Address(detail, $"{net}.110").GetProperty("reservation").GetString().Should().Be("DHCP");
        Address(detail, $"{net}.2").GetProperty("state").GetString().Should().Be("libre");
        detail.GetProperty("suggested").GetString().Should().Be($"{net}.2");

        // Una IP que cambia de MAC se avisa.
        await JsonAsync(await admin.PostAsync($"/api/v1/red/subredes/{subnetId}/escanear", null));
        await NextOfTypeAsync(socket, "barrido_ip");
        await SendAckedAsync(socket, "barrido_ip_resultado", new
        {
            subredId = subnetId,
            ok = true,
            responden = Array.Empty<string>(),
            arp = new[] { new { ip = $"{net}.60", mac = "3C:52:82:00:00:AA" } },
        });
        var changed = Address(await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}")), $"{net}.60");
        changed.GetProperty("macChanged").GetBoolean().Should().BeTrue();
        changed.GetProperty("previousMac").GetString().Should().Be("3C:52:82:00:00:99");

        // Historial: la .60 la tuvieron dos equipos (lo más reciente primero); la .31, la PC por su agente.
        var history = (await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}/historial?ip={net}.60&dias=30"))).EnumerateArray().ToList();
        history.Select(h => h.GetProperty("mac").GetString()).Should().Equal("3C:52:82:00:00:AA", "3C:52:82:00:00:99");
        history[0].GetProperty("current").GetBoolean().Should().BeTrue();
        var pcHistory = (await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}/historial?ip={net}.31"))).EnumerateArray().Single();
        pcHistory.GetProperty("machineId").GetInt32().Should().Be(pc.MachineId);
        pcHistory.GetProperty("source").GetString().Should().Be("agente");
        (await admin.GetAsync($"/api/v1/red/subredes/{subnetId}/historial?ip=no-es-ip")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Una muestra más vieja que el período vigente (cola atrasada del agente, reloj del equipo detrás del servidor)
        // no lo acorta: antes dejaba "hasta" antes de "desde", violaba el CHECK y el agente reenviaba sin fin.
        await _api.ExecuteAsync(
            "UPDATE dbo.historial_ip SET desde = DATEADD(HOUR, 1, SYSUTCDATETIME()), hasta = DATEADD(HOUR, 1, SYSUTCDATETIME()) " +
            "WHERE subred_id = @s AND ip = @ip", ("@s", subnetId), ("@ip", $"{net}.70"));
        await JsonAsync(await admin.PostAsync($"/api/v1/red/subredes/{subnetId}/escanear", null));
        await NextOfTypeAsync(socket, "barrido_ip");
        await SendAckedAsync(socket, "barrido_ip_resultado", new
        {
            subredId = subnetId,
            ok = true,
            responden = Array.Empty<string>(),
            arp = new[] { new { ip = $"{net}.70", mac = printerMac } },
        });
        var invalid = await _api.ScalarAsync(
            "SELECT COUNT(*) FROM dbo.historial_ip WHERE subred_id = @s AND ip = @ip AND hasta < desde", ("@s", subnetId), ("@ip", $"{net}.70"));
        Convert.ToInt32(invalid).Should().Be(0);
        (await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}/historial?ip={net}.70"))).EnumerateArray().Should().ContainSingle();
    }

    [DatabaseFact]
    public async Task Subnets_cannot_overlap_and_must_be_small_enough_to_sweep()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var branch = await SantiagoAsync();
        var net = NewNetwork();
        object Subnet(string red) => new { nombre = $"Red {red}", sucursalId = branch, red, barridoHoras = 0, diasLibre = 30 };

        await JsonAsync(await admin.PostAsJsonAsync("/api/v1/red/subredes", Subnet($"{net}.0/24")), HttpStatusCode.Created);
        (await admin.PostAsJsonAsync("/api/v1/red/subredes", Subnet($"{net}.128/25"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsJsonAsync("/api/v1/red/subredes", Subnet("10.0.0.0/16"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [DatabaseFact]
    public async Task A_collector_cannot_report_a_subnet_that_is_not_its_own()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var net = NewNetwork();
        var owner = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var other = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var subnetId = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/red/subredes", new
        {
            nombre = "Ajena",
            sucursalId = await SantiagoAsync(),
            red = $"{net}.0/24",
            recolectorId = owner.AgentId,
            barridoHoras = 0,
            diasLibre = 30,
        }), HttpStatusCode.Created)).GetProperty("id").GetInt32();

        using var socket = await ConnectAndGreetAsync(_api, other);
        await SendAckedAsync(socket, "barrido_ip_resultado", new { subredId = subnetId, ok = true, responden = new[] { $"{net}.5" } });

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}"));
        Address(detail, $"{net}.5").GetProperty("state").GetString().Should().Be("libre");
        detail.GetProperty("subnet").GetProperty("lastSweepAt").ValueKind.Should().Be(JsonValueKind.Null);
    }
}

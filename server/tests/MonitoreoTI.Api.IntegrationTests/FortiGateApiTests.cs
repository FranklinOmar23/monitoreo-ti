using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Network;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// API REST del FortiGate (decisión 76): el token se guarda cifrado y viaja sellado al recolector; "Probar API" hace la
/// ida y vuelta con el agente; lo que el agente lee (DHCP y dispositivos) llena nombres, sistema, DHCP/fija y las
/// reservas del rango del DHCP. Sin token, todo sigue igual que antes.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FortiGateApiTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private const string Token = "h7k2m9p4q1w8e5r3t6y0u2i4o7p1as";
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

    private async Task<int> ModelAsync() =>
        (int)(await _api.ScalarAsync("SELECT id FROM dbo.modelos_dispositivo WHERE modelo = 'Catalyst 9200-48P' AND empresa_id IS NULL"))!;

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

    private static JsonElement Address(JsonElement detail, string ip) =>
        detail.GetProperty("addresses").EnumerateArray().Single(a => a.GetProperty("ip").GetString() == ip);

    [DatabaseFact]
    public async Task The_token_is_sealed_for_the_collector_tested_through_it_and_its_data_fills_the_ip_inventory()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var branch = await SantiagoAsync();
        var net = $"10.{Random.Shared.Next(100, 250)}.{Random.Shared.Next(0, 255)}";
        var collector = await _api.RegisterNewAgentAsync(Company, "Santiago");
        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.3.6' WHERE id = @id", ("@id", collector.AgentId));

        var subnetId = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/red/subredes", new
        {
            nombre = "VLAN3",
            sucursalId = branch,
            red = $"{net}.0/24",
            recolectorId = collector.AgentId,
            barridoHoras = 0,
            diasLibre = 30,
        }), HttpStatusCode.Created)).GetProperty("id").GetInt32();

        // Un token mal pegado se rechaza antes de guardar.
        var body = new Dictionary<string, object?>
        {
            ["nombre"] = $"PRUEBA-FGT-{Random.Shared.Next(100000)}",
            ["modeloId"] = await ModelAsync(),
            ["sucursalId"] = branch,
            ["ipGestion"] = $"{net}.3",
            ["snmpUsuario"] = "Centinela",
            ["snmpClaveAutenticacion"] = "auth-secreta-123",
            ["snmpClavePrivacidad"] = "priv-secreta-456",
            ["recolectorId"] = collector.AgentId,
            ["apiUrl"] = $"{net}.3:12443",
            ["apiToken"] = "token con espacios",
        };
        (await admin.PostAsJsonAsync("/api/v1/red/dispositivos", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body["apiToken"] = Token;
        var deviceId = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/red/dispositivos", body), HttpStatusCode.Created))
            .GetProperty("id").GetInt32();

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{deviceId}"));
        detail.GetProperty("apiUrl").GetString().Should().Be($"https://{net}.3:12443");
        detail.GetProperty("hasApiToken").GetBoolean().Should().BeTrue();
        detail.GetRawText().Should().NotContain(Token);

        // El recolector recibe la API con el token sellado para él.
        using var socket = await ConnectAsync(_api, collector.Credential);
        await SendAsync(socket, "hola", new { version = "0.3.6", hostname = "RECOLECTOR", so = "Windows", es_recolector = true });
        await ReceiveAsync(socket); // ack
        var welcome = (await ReceiveAsync(socket))!.Value.GetProperty("datos");
        welcome.GetRawText().Should().NotContain(Token);
        var api = welcome.GetProperty("recolector").GetProperty("dispositivos").EnumerateArray()
            .Single(d => d.GetProperty("id").GetInt32() == deviceId).GetProperty("api");
        api.GetProperty("url").GetString().Should().Be($"https://{net}.3:12443");
        SnmpCredentialSeal.Open(collector.Credential, api.GetProperty("token").GetString()!).Should().Contain(Token);

        // "Probar API": el servidor espera la respuesta del agente con el mismo id.
        var test = admin.PostAsJsonAsync($"/api/v1/red/dispositivos/{deviceId}/probar-api", new { apiUrl = (string?)null, apiToken = (string?)null, apiAceptarCertificado = true });
        var asked = await NextOfTypeAsync(socket, "probar_api");
        SnmpCredentialSeal.Open(collector.Credential, asked.GetProperty("token").GetString()!).Should().Contain(Token);
        await SendAsync(socket, "api_prueba_resultado", new
        {
            solicitudId = asked.GetProperty("solicitud_id").GetString(),
            ok = true,
            modelo = "FortiGate 200G",
            version = "v7.4.4 build 2662",
            nombre = "FGT-SD-01",
            serie = "FG200G0001",
            ms = 42,
        });
        var tested = await JsonAsync(await test);
        tested.GetProperty("ok").GetBoolean().Should().BeTrue();
        tested.GetProperty("model").GetString().Should().Be("FortiGate 200G");

        // Lo que leyó de la API tras un sondeo.
        await SendAckedAsync(socket, "fortigate_datos", new
        {
            dispositivoId = deviceId,
            ok = true,
            dhcp = new object[]
            {
                new { ip = $"{net}.40", mac = "AA:BB:CC:00:00:40", nombre = "CONTA-PC03", vence = 1_900_000_000L, interfaz = "VLAN3-x3", reservada = false },
                new { ip = $"{net}.45", mac = "AA:BB:CC:00:00:45", nombre = "APAGADA", vence = 1_900_000_000L, interfaz = "VLAN3-x3", reservada = false },
            },
            servidoresDhcp = new[]
            {
                new
                {
                    interfaz = "VLAN3-x3",
                    rangos = new[] { new { desde = $"{net}.100", hasta = $"{net}.200" } },
                    reservadas = new[] { new { ip = $"{net}.10", mac = "00:11:22:33:44:55", descripcion = "Impresora" } },
                },
            },
            dispositivos = new[]
            {
                new { ip = $"{net}.40", mac = "AA:BB:CC:00:00:40", nombre = "CONTA-PC03", sistema = "Windows 10", interfaz = "VLAN3-x3" },
                new { ip = $"{net}.41", mac = "AA:BB:CC:00:00:41", nombre = "SERVIDOR", sistema = "Linux", interfaz = "VLAN3-x3" },
            },
        });

        var subnet = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}"));
        var dhcp = Address(subnet, $"{net}.40");
        dhcp.GetProperty("hostname").GetString().Should().Be("CONTA-PC03");
        dhcp.GetProperty("hostnameSource").GetString().Should().Be("fortigate");
        dhcp.GetProperty("operatingSystem").GetString().Should().Be("Windows 10");
        dhcp.GetProperty("assignment").GetString().Should().Be("dhcp");
        Address(subnet, $"{net}.41").GetProperty("assignment").GetString().Should().Be("fija");
        // Una concesión de un equipo apagado no la marca en uso.
        Address(subnet, $"{net}.45").GetProperty("lastSeenAt").ValueKind.Should().Be(JsonValueKind.Null);

        var reservations = subnet.GetProperty("reservations").EnumerateArray().ToList();
        reservations.Should().Contain(r => r.GetProperty("from").GetString() == $"{net}.100" && r.GetProperty("origin").GetString() == "fortigate");
        var printer = reservations.Single(r => r.GetProperty("from").GetString() == $"{net}.10");
        printer.GetProperty("reason").GetString().Should().Contain("Impresora");
        (await admin.DeleteAsync($"/api/v1/red/subredes/{subnetId}/reservas/{printer.GetProperty("id").GetInt32()}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // La siguiente lectura sin la reserva de la impresora la quita; el rango sigue.
        await SendAckedAsync(socket, "fortigate_datos", new
        {
            dispositivoId = deviceId,
            ok = true,
            servidoresDhcp = new[] { new { interfaz = "VLAN3-x3", rangos = new[] { new { desde = $"{net}.100", hasta = $"{net}.200" } } } },
        });
        subnet = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}"));
        subnet.GetProperty("reservations").EnumerateArray().Select(r => r.GetProperty("from").GetString())
            .Should().BeEquivalentTo([$"{net}.100"]);

        detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{deviceId}"));
        detail.GetProperty("apiLastOkAt").ValueKind.Should().NotBe(JsonValueKind.Null);

        // Quitar la dirección quita la API: el recolector vuelve a solo SNMP.
        body["apiUrl"] = "";
        body["apiToken"] = null;
        body["snmpClaveAutenticacion"] = null;
        body["snmpClavePrivacidad"] = null;
        await JsonAsync(await admin.PutAsJsonAsync($"/api/v1/red/dispositivos/{deviceId}", body));
        detail = await JsonAsync(await admin.GetAsync($"/api/v1/red/dispositivos/{deviceId}"));
        detail.GetProperty("hasApiToken").GetBoolean().Should().BeFalse();
        (await admin.PostAsJsonAsync($"/api/v1/red/dispositivos/{deviceId}/probar-api", new { apiUrl = (string?)null, apiToken = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

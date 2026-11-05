using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Network;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// Instalación remota del agente (decisión 77): el técnico pide instalar en IP de la subred; el recolector recibe la
/// cuenta y un token de un solo uso sellados para él, informa los pasos, y el equipo que se registra con ese token cierra
/// la instalación. La clave no queda en ningún lado del servidor.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RemoteInstallTests(TestDatabase database) : IAsyncLifetime
{
    private const string Company = "Grupo Comercial Caribe";
    private const string Password = "Clave-Del-Dominio-123";
    private static readonly ECDsa ProductKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private AgentTestApi _api = null!;

    public Task InitializeAsync()
    {
        if (TestDatabase.ServerConnectionString is not null)
            _api = new AgentTestApi(database, new Dictionary<string, string>
            {
                // Cualquier binario con versión sirve de "instalador publicado".
                ["Instaladores:Archivo"] = typeof(RemoteInstallTests).Assembly.Location,
                ["Actualizaciones:LlavePrivada"] = Convert.ToBase64String(ProductKey.ExportPkcs8PrivateKey()),
            });
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
    public async Task The_collector_installs_with_a_sealed_account_and_the_new_agent_closes_the_install()
    {
        var token = await _api.TokenForAsync(Company, "Administrador");
        var admin = _api.ClientWith(token);
        var email = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;
        var branch = (int)(await _api.ScalarAsync(
            "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = 'Santiago'", ("@e", Company)))!;
        var net = $"10.{Random.Shared.Next(100, 250)}.{Random.Shared.Next(0, 255)}";
        var collector = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var subnetId = (await JsonAsync(await admin.PostAsJsonAsync("/api/v1/red/subredes", new
        {
            nombre = "VLAN4",
            sucursalId = branch,
            red = $"{net}.0/24",
            recolectorId = collector.AgentId,
            barridoHoras = 0,
            diasLibre = 30,
        }), HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var body = new { ips = new[] { $"{net}.6", $"{net}.7" }, cuenta = @"CARIBE\soporte", clave = Password };

        // Sin 2FA reciente no se puede.
        await _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = NULL WHERE correo = @c", ("@c", email));
        (await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/instalar-agente", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = SYSUTCDATETIME() WHERE correo = @c", ("@c", email));

        // Un recolector viejo no sabe instalar.
        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.3.6' WHERE id = @id", ("@id", collector.AgentId));
        (await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/instalar-agente", body)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Una IP fuera de la subred se rechaza.
        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.3.7' WHERE id = @id", ("@id", collector.AgentId));
        (await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/instalar-agente", body with { ips = ["192.0.2.10"] }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Recolector desconectado: 409 y las instalaciones quedan en error.
        (await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/instalar-agente", body with { ips = [$"{net}.9"] }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var socket = await ConnectAsync(_api, collector.Credential);
        await SendAsync(socket, "hola", new { version = "0.3.7", hostname = "RECOLECTOR", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        await ReceiveAsync(socket); // bienvenida

        await JsonAsync(await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/instalar-agente", body), HttpStatusCode.Created);
        var first = await NextOfTypeAsync(socket, "instalar_agente");
        var second = await NextOfTypeAsync(socket, "instalar_agente");
        first.GetProperty("ip").GetString().Should().Be($"{net}.6");
        first.GetRawText().Should().NotContain(Password);
        first.GetProperty("instalador").GetProperty("url").GetString().Should().Be("/agent/actualizacion");
        SnmpCredentialSeal.Open(collector.Credential, first.GetProperty("cuenta").GetString()!).Should().Contain(Password).And.Contain("soporte");
        var enrollToken = JsonDocument.Parse(SnmpCredentialSeal.Open(collector.Credential, first.GetProperty("token").GetString()!))
            .RootElement.GetProperty("token").GetString()!;

        // La clave no quedó en el servidor (ni en la tabla ni en la auditoría).
        ((int)(await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.instalaciones_remotas WHERE cuenta LIKE '%' + @p + '%' OR detalle LIKE '%' + @p + '%'", ("@p", Password)))!)
            .Should().Be(0);
        ((int)(await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE detalle LIKE '%' + @p + '%'", ("@p", Password)))!).Should().Be(0);

        // El recolector informa: la .6 se instala, la .7 es Windows 7 y se omite.
        var firstId = first.GetProperty("instalacion_id").GetInt32();
        var secondId = second.GetProperty("instalacion_id").GetInt32();
        await SendAckedAsync(socket, "instalacion_estado", new { instalacionId = firstId, estado = "copiando", sistema = "Windows 10 (10.0.19045) 64 bits" });
        await SendAckedAsync(socket, "instalacion_estado", new { instalacionId = firstId, estado = "instalado", detalle = "Falta que se registre." });
        await SendAckedAsync(socket, "instalacion_estado", new { instalacionId = secondId, estado = "omitido", detalle = "Windows 7: el agente actual necesita Windows 10 o superior.", sistema = "Windows 7 (6.1.7601) 64 bits" });
        // Un estado que no existe se ignora.
        await SendAckedAsync(socket, "instalacion_estado", new { instalacionId = firstId, estado = "registrado" });

        var list = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}/instalaciones"));
        var six = list.EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == firstId);
        six.GetProperty("status").GetString().Should().Be("instalado");
        six.GetProperty("operatingSystem").GetString().Should().StartWith("Windows 10");
        six.GetProperty("account").GetString().Should().Be(@"CARIBE\soporte");
        list.EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == secondId).GetProperty("status").GetString().Should().Be("omitido");
        list.EnumerateArray().Should().Contain(i => i.GetProperty("ip").GetString() == $"{net}.9" && i.GetProperty("status").GetString() == "error");

        // No se puede volver a pedir la .6 mientras está en curso.
        (await admin.PostAsJsonAsync($"/api/v1/red/subredes/{subnetId}/instalar-agente", body with { ips = [$"{net}.6"] }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);

        // El equipo nuevo se registra con su token: la instalación queda cerrada con el equipo.
        var mac = AgentTestApi.NewMac();
        (await _api.RegisterAsync(enrollToken, mac, "PC-MONITOREO003")).EnsureSuccessStatusCode();
        list = await JsonAsync(await admin.GetAsync($"/api/v1/red/subredes/{subnetId}/instalaciones"));
        six = list.EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == firstId);
        six.GetProperty("status").GetString().Should().Be("registrado");
        six.GetProperty("machineName").GetString().Should().Be("PC-MONITOREO003");

        // El token era de un solo uso, y el de la instalación omitida quedó revocado.
        (await _api.RegisterAsync(enrollToken, AgentTestApi.NewMac())).StatusCode.Should().NotBe(HttpStatusCode.OK);
        var secondToken = JsonDocument.Parse(SnmpCredentialSeal.Open(collector.Credential, second.GetProperty("token").GetString()!))
            .RootElement.GetProperty("token").GetString()!;
        (await _api.RegisterAsync(secondToken, AgentTestApi.NewMac())).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}

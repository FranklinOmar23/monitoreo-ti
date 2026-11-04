using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Fase 6.2: sesiones remotas por el relay (sección 10.1) e historial (W-34).</summary>
[Collection(DatabaseCollection.Name)]
public sealed class RemoteSessionTests(TestDatabase database) : IAsyncLifetime
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

    private async Task<HttpClient> TechnicianAsync()
    {
        var token = await _api.TokenForAsync(Company, "Tecnico");
        var email = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("email").Value;
        await _api.ExecuteAsync("UPDATE dbo.usuarios SET ultimo_2fa = SYSUTCDATETIME() WHERE correo = @c", ("@c", email));
        return _api.ClientWith(token);
    }

    private async Task<WebSocket> RelayAsync(string path, string token)
    {
        var socket = await _api.Factory.Server.CreateWebSocketClient().ConnectAsync(new Uri($"ws://localhost{path}"), CancellationToken.None);
        await SendTextAsync(socket, JsonSerializer.Serialize(new { token }));
        return socket;
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<(WebSocketMessageType Type, byte[] Data)?> ReceiveFrameAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[64 * 1024];
        using var data = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, data.ToArray());
    }

    [DatabaseFact]
    public async Task Panel_and_agent_meet_in_the_relay_and_the_session_is_recorded_when_it_ends()
    {
        var tech = await TechnicianAsync();
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using var agentSocket = await ConnectAndGreetAsync(_api, agent);

        var ticket = await (await tech.PostAsJsonAsync("/api/v1/sesiones-remotas", new { maquinaId = agent.MachineId, tipo = "terminal" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = ticket.GetProperty("sessionId").GetInt64();
        var path = ticket.GetProperty("relayPath").GetString()!;
        path.Should().Be($"/relay/{sessionId}");

        // El agente recibe la orden con su propio token (distinto del del panel).
        var open = (await ReceiveAsync(agentSocket))!.Value;
        open.GetProperty("tipo").GetString().Should().Be("abrir_sesion_remota");
        var agentToken = open.GetProperty("datos").GetProperty("token_un_uso").GetString()!;
        agentToken.Should().NotBe(ticket.GetProperty("token").GetString());
        open.GetProperty("datos").GetProperty("tipo").GetString().Should().Be("terminal");

        using var panel = await RelayAsync(path, ticket.GetProperty("token").GetString()!);
        using var relayAgent = await RelayAsync(path, agentToken);
        var connected = (await ReceiveFrameAsync(panel))!.Value;
        Encoding.UTF8.GetString(connected.Data).Should().Contain("\"relay\":\"conectado\"");

        // El token del panel no sirve dos veces.
        using var intruder = await RelayAsync(path, ticket.GetProperty("token").GetString()!);
        (await ReceiveFrameAsync(intruder)).Should().BeNull();

        // Los marcos pasan tal cual en ambos sentidos (texto y binario).
        await SendTextAsync(panel, "dir\r");
        var toAgent = (await ReceiveFrameAsync(relayAgent))!.Value;
        Encoding.UTF8.GetString(toAgent.Data).Should().Be("dir\r");
        await relayAgent.SendAsync(new byte[] { 1, 2, 3 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        var toPanel = (await ReceiveFrameAsync(panel))!.Value;
        toPanel.Type.Should().Be(WebSocketMessageType.Binary);
        toPanel.Data.Should().Equal(1, 2, 3);

        var history = await (await tech.GetAsync($"/api/v1/sesiones-remotas?maquina={agent.MachineId}")).Content.ReadFromJsonAsync<JsonElement>();
        history.GetProperty("items")[0].GetProperty("active").GetBoolean().Should().BeTrue();

        // Cerrar desde el panel: el agente recibe la orden y queda la duración en el historial.
        (await tech.PostAsync($"/api/v1/sesiones-remotas/{sessionId}/cerrar", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var closing = Encoding.UTF8.GetString((await ReceiveFrameAsync(panel))!.Value.Data);
        closing.Should().Contain("\"relay\":\"cerrada\"");
        (await ReceiveAsync(agentSocket))!.Value.GetProperty("tipo").GetString().Should().Be("cerrar_sesion_remota");

        history = await (await tech.GetAsync($"/api/v1/sesiones-remotas?maquina={agent.MachineId}")).Content.ReadFromJsonAsync<JsonElement>();
        var item = history.GetProperty("items")[0];
        item.GetProperty("type").GetString().Should().Be("terminal");
        item.GetProperty("endedAt").ValueKind.Should().Be(JsonValueKind.String);
        item.GetProperty("active").GetBoolean().Should().BeFalse();
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'sesion.cerrar' AND detalle LIKE @d",
            ("@d", $"%\"sesionId\":{sessionId},%"))).Should().Be(1);
    }

    [DatabaseFact]
    public async Task File_operations_reported_by_the_agent_are_audited_for_the_technician_of_the_session()
    {
        var tech = await TechnicianAsync();
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var other = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using var agentSocket = await ConnectAndGreetAsync(_api, agent);
        using var otherSocket = await ConnectAndGreetAsync(_api, other);

        var ticket = await (await tech.PostAsJsonAsync("/api/v1/sesiones-remotas", new { maquinaId = agent.MachineId, tipo = "archivos" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = ticket.GetProperty("sessionId").GetInt64();
        await ReceiveAsync(agentSocket); // abrir_sesion_remota

        await SendAckedAsync(agentSocket, "actividad_sesion", new { sesionId = sessionId, accion = "descargar", ruta = "C:/Reportes/ventas.xlsx", tamano = 2048 });
        // Otro equipo no puede atribuir actividad a una sesión ajena.
        await SendAckedAsync(otherSocket, "actividad_sesion", new { sesionId = sessionId, accion = "eliminar", ruta = "C:/Windows" });

        var userId = (int)(await _api.ScalarAsync("SELECT usuario_id FROM dbo.sesiones_remotas WHERE id = @s", ("@s", sessionId)))!;
        var detail = (string?)await _api.ScalarAsync(
            "SELECT detalle FROM dbo.auditoria WHERE accion = 'archivo.descargar' AND usuario_id = @u AND entidad_id = @m",
            ("@u", userId), ("@m", agent.MachineId.ToString()));
        detail.Should().Contain("ventas.xlsx").And.Contain("2048");
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.auditoria WHERE accion = 'archivo.eliminar' AND usuario_id = @u", ("@u", userId)))
            .Should().Be(0);
    }

    [DatabaseFact]
    public async Task The_agent_is_told_who_connects_and_whether_the_user_must_accept_the_desktop_session()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var tech = await TechnicianAsync();
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using var agentSocket = await ConnectAndGreetAsync(_api, agent);

        try
        {
            (await admin.PutAsJsonAsync("/api/v1/sesiones-remotas/configuracion", new { pedirPermiso = true })).StatusCode.Should().Be(HttpStatusCode.OK);
            // El técnico no cambia esa configuración.
            (await tech.PutAsJsonAsync("/api/v1/sesiones-remotas/configuracion", new { pedirPermiso = false })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var desktop = await tech.PostAsJsonAsync("/api/v1/sesiones-remotas", new
            {
                maquinaId = agent.MachineId,
                tipo = "escritorio",
                motivo = "Respaldar disco · TCK-1042",
                soloVer = true,
            });
            desktop.StatusCode.Should().Be(HttpStatusCode.Created, await desktop.Content.ReadAsStringAsync());
            var open = (await ReceiveAsync(agentSocket))!.Value.GetProperty("datos");
            open.GetProperty("tipo").GetString().Should().Be("escritorio");
            open.GetProperty("tecnico").GetString().Should().Be("Usuario de prueba");
            open.GetProperty("pedir_permiso").GetBoolean().Should().BeTrue();
            open.GetProperty("solo_ver").GetBoolean().Should().BeTrue();
            // El técnico tiene remoto.archivos: puede transferir archivos dentro del escritorio.
            open.GetProperty("archivos").GetBoolean().Should().BeTrue();

            var history = await (await tech.GetAsync($"/api/v1/sesiones-remotas?maquina={agent.MachineId}")).Content.ReadFromJsonAsync<JsonElement>();
            history.GetProperty("items")[0].GetProperty("reason").GetString().Should().Be("Respaldar disco · TCK-1042");
            history.GetProperty("items")[0].GetProperty("viewOnly").GetBoolean().Should().BeTrue();
        }
        finally
        {
            await _api.ExecuteAsync("UPDATE dbo.empresas SET pedir_permiso_remoto = 0");
        }
    }

    [DatabaseFact]
    public async Task A_session_needs_the_agent_online_and_the_permission_of_its_type()
    {
        var tech = await TechnicianAsync();
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");

        var offline = await tech.PostAsJsonAsync("/api/v1/sesiones-remotas", new { maquinaId = agent.MachineId, tipo = "archivos" });
        offline.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.sesiones_remotas WHERE maquina_id = @m", ("@m", agent.MachineId))).Should().Be(0);

        // El rol Red no tiene permisos remotos.
        var network = _api.ClientWith(await _api.TokenForAsync(Company, "Red"));
        (await network.PostAsJsonAsync("/api/v1/sesiones-remotas", new { maquinaId = agent.MachineId, tipo = "terminal" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}

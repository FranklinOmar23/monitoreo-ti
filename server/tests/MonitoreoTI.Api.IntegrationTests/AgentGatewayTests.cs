using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Workers;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class AgentGatewayTests(TestDatabase database) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
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

    [DatabaseFact]
    public async Task Connection_without_a_valid_credential_is_refused()
    {
        var agent = await _api.RegisterNewAgentAsync();
        var otherCompanyId = await database.GetCompanyIdAsync("Clínica San Rafael");
        var secret = agent.Credential[(agent.Credential.IndexOf('.') + 1)..];

        foreach (var credential in new[] { null, "", "basura", AgentTokens.Generate(1).Token, $"{otherCompanyId}.{secret}" })
        {
            var act = () => ConnectAsync(_api, credential);
            await act.Should().ThrowAsync<Exception>($"la credencial '{credential}' no es válida");
        }
    }

    [DatabaseFact]
    public async Task Hello_gets_an_ack_and_a_welcome_and_brings_the_machine_online()
    {
        var agent = await _api.RegisterNewAgentAsync();
        (await _api.QueryMachineAsync(agent.Mac)).Status.Should().Be("desconectado");

        using var socket = await ConnectAsync(_api, agent.Credential);
        var helloId = await SendAsync(socket, "hola", new { version = "9.9.9", hostname = "PC-PRUEBA", so = "Windows 11", es_recolector = false });

        var ack = await ReceiveAsync(socket);
        ack!.Value.GetProperty("tipo").GetString().Should().Be("ack");
        ack.Value.GetProperty("datos").GetProperty("id").GetString().Should().Be(helloId);

        var welcome = await ReceiveAsync(socket);
        welcome!.Value.GetProperty("tipo").GetString().Should().Be("bienvenida");
        welcome.Value.GetProperty("datos").GetProperty("heartbeat_s").GetInt32().Should().Be(30);

        var machine = await _api.QueryMachineAsync(agent.Mac);
        machine.Status.Should().Be("sano");
        machine.AgentVersion.Should().Be("9.9.9");
        machine.LastContact.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        Tracker.ConnectedCount.Should().BeGreaterThan(0);
    }

    [DatabaseFact]
    public async Task Heartbeats_are_acknowledged()
    {
        var agent = await _api.RegisterNewAgentAsync();
        using var socket = await ConnectAndGreetAsync(agent);

        var id = await SendAsync(socket, "heartbeat", new { uptimeSeconds = 12 });

        var ack = await ReceiveAsync(socket);
        ack!.Value.GetProperty("tipo").GetString().Should().Be("ack");
        ack.Value.GetProperty("datos").GetProperty("id").GetString().Should().Be(id);
    }

    [DatabaseFact]
    public async Task The_first_message_must_be_hello()
    {
        var agent = await _api.RegisterNewAgentAsync();
        using var socket = await ConnectAsync(_api, agent.Credential);

        await SendAsync(socket, "heartbeat", new { });

        (await ReceiveAsync(socket)).Should().BeNull("el servidor cierra la conexión");
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
    }

    [DatabaseFact]
    public async Task Malformed_messages_close_the_connection()
    {
        var agent = await _api.RegisterNewAgentAsync();
        using var socket = await ConnectAndGreetAsync(agent);

        await socket.SendAsync(Encoding.UTF8.GetBytes("esto no es json"), WebSocketMessageType.Text, true, CancellationToken.None);

        (await ReceiveAsync(socket)).Should().BeNull();
    }

    [DatabaseFact]
    public async Task A_new_connection_replaces_the_previous_one_of_the_same_agent()
    {
        var agent = await _api.RegisterNewAgentAsync();
        using var first = await ConnectAndGreetAsync(agent);
        using var second = await ConnectAndGreetAsync(agent);

        (await ReceiveAsync(first)).Should().BeNull("la conexión anterior se cierra");
        first.CloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);

        await SendAsync(second, "heartbeat", new { });
        (await ReceiveAsync(second))!.Value.GetProperty("tipo").GetString().Should().Be("ack");
    }

    [DatabaseFact]
    public async Task Revoked_agents_retired_machines_and_suspended_companies_cannot_connect()
    {
        var revoked = await _api.RegisterNewAgentAsync();
        await _api.ExecuteAsync("UPDATE dbo.agentes SET revocado = 1 WHERE id = @id", ("@id", revoked.AgentId));
        var retired = await _api.RegisterNewAgentAsync();
        await _api.ExecuteAsync("UPDATE dbo.maquinas SET estado = 'baja' WHERE id = @id", ("@id", retired.MachineId));

        foreach (var agent in new[] { revoked, retired })
        {
            var act = () => ConnectAsync(_api, agent.Credential);
            await act.Should().ThrowAsync<Exception>();
        }

        var tenant = await _api.RegisterNewAgentAsync("Clínica San Rafael", "Santo Domingo");
        await _api.ExecuteAsync("UPDATE dbo.empresas SET estado = 'suspendida' WHERE nombre = N'Clínica San Rafael'");
        try
        {
            var act = () => ConnectAsync(_api, tenant.Credential);
            await act.Should().ThrowAsync<Exception>();
        }
        finally
        {
            await _api.ExecuteAsync("UPDATE dbo.empresas SET estado = 'activa' WHERE nombre = N'Clínica San Rafael'");
        }
    }

    [DatabaseFact]
    public async Task Silent_connections_are_closed_by_the_idle_timeout()
    {
        await using var api = new AgentTestApi(database, new() { ["Agents:Gateway:IdleTimeout"] = "00:00:01" });
        var agent = await api.RegisterNewAgentAsync();
        using var socket = await ConnectAsync(api, agent.Credential);
        await SendAsync(socket, "hola", new { version = "0.1.0", es_recolector = false });
        await ReceiveAsync(socket);
        await ReceiveAsync(socket);

        (await ReceiveAsync(socket)).Should().BeNull("sin heartbeat el servidor cierra la conexión");
    }

    [DatabaseFact]
    public async Task Machines_without_contact_for_three_minutes_become_disconnected()
    {
        var agent = await _api.RegisterNewAgentAsync();
        using var socket = await ConnectAndGreetAsync(agent);
        (await _api.QueryMachineAsync(agent.Mac)).Status.Should().Be("sano");

        // Conectado: aunque la base tenga un contacto viejo, el ciclo guarda primero el contacto en memoria.
        await MakeContactOldAsync(agent);
        await TickAsync();
        (await _api.QueryMachineAsync(agent.Mac)).Status.Should().Be("sano");

        // Apagado el agente, nada refresca el contacto y el siguiente ciclo la marca desconectada.
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await WaitUntilAsync(() => Tracker.ConnectedCount == 0);
        await MakeContactOldAsync(agent);
        await TickAsync();

        (await _api.QueryMachineAsync(agent.Mac)).Status.Should().Be("desconectado");
    }

    [DatabaseFact]
    public async Task The_detector_only_touches_its_own_company_and_agentless_machines()
    {
        var agent = await _api.RegisterNewAgentAsync();
        using var socket = await ConnectAndGreetAsync(agent);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        await WaitUntilAsync(() => Tracker.ConnectedCount == 0);
        await MakeContactOldAsync(agent);

        await TickAsync();

        // IMP-RECEPCION es una impresora sin agente: nunca pasa a desconectado por falta de heartbeat.
        (await _api.ScalarAsync("SELECT estado FROM dbo.maquinas WHERE nombre = 'IMP-RECEPCION'")).Should().Be("sano");
    }

    private AgentPresenceTracker Tracker => _api.Factory.Services.GetRequiredService<AgentPresenceTracker>();

    private bool IsTracked(RegisteredAgent agent) => _api.Factory.Services.GetRequiredService<MonitoreoTI.Api.Agents.AgentConnectionRegistry>().Get(agent.AgentId) is not null;

    private Task TickAsync() =>
        _api.Factory.Services.GetServices<IHostedService>().OfType<AgentPresenceService>().Single().TickAsync(CancellationToken.None);

    private Task MakeContactOldAsync(RegisteredAgent agent) =>
        _api.ExecuteAsync("UPDATE dbo.agentes SET ultimo_contacto = DATEADD(HOUR, -7, SYSUTCDATETIME()) WHERE id = @id", ("@id", agent.AgentId));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("La condición no se cumplió a tiempo.");
            await Task.Delay(25);
        }
    }

    private Task<WebSocket> ConnectAndGreetAsync(RegisteredAgent agent) => AgentSocket.ConnectAndGreetAsync(_api, agent);
}

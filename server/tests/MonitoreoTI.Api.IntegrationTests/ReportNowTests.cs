using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Decisión 85: "Actualizar" en la ficha le pide al agente conectado que reporte ya.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReportNowTests(TestDatabase database) : IAsyncLifetime
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

    private static async Task<string> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    [DatabaseFact]
    public async Task The_connected_agent_is_asked_to_report_and_requests_are_throttled()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var path = $"/api/v1/maquinas/{agent.MachineId}/actualizar";

        // Sin conexión (con un agente que sí lo entendería).
        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.4.2' WHERE id = @id", ("@id", agent.AgentId));
        var offline = await admin.PostAsync(path, null);
        offline.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(offline)).Should().Contain("no está conectado");

        using var socket = await ConnectAndGreetAsync(_api, agent);
        // Al saludar quedó con su versión (0.1.0): un agente viejo no entiende el pedido, se dice en vez de quedar esperando.
        var old = await admin.PostAsync(path, null);
        old.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(old)).Should().Contain("Actualícelo");

        await _api.ExecuteAsync("UPDATE dbo.agentes SET version = '0.4.2' WHERE id = @id", ("@id", agent.AgentId));
        var sent = await admin.PostAsync(path, null);
        sent.StatusCode.Should().Be(HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enviado").GetBoolean().Should().BeTrue();
        string? received = null;
        for (var i = 0; i < 5 && received != "reportar_ahora"; i++)
            received = (await ReceiveAsync(socket))?.GetProperty("tipo").GetString();
        received.Should().Be("reportar_ahora");

        // Otro pedido enseguida no se manda.
        var again = await (await admin.PostAsync(path, null)).Content.ReadFromJsonAsync<JsonElement>();
        again.GetProperty("enviado").GetBoolean().Should().BeFalse();
        again.GetProperty("segundos").GetInt32().Should().BeInRange(1, 10);

        // Un equipo que no existe (o no se ve).
        (await admin.PostAsync("/api/v1/maquinas/999999/actualizar", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

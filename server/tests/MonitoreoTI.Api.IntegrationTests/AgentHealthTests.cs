using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Infrastructure.Agents;
using MonitoreoTI.Infrastructure.Health;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Mensajes de salud del agente (Fase 3) y el motor de alertas que los convierte en problemas de la ficha.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentHealthTests(TestDatabase database) : IAsyncLifetime
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

    private async Task<(RegisteredAgent Agent, System.Net.WebSockets.WebSocket Socket)> ConnectAgentAsync()
    {
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        var socket = await ConnectAndGreetAsync(_api, agent);
        // Una métrica reciente: el motor solo mira el disco y los servicios de equipos que reportan.
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 30, disco = 50 });
        await _api.Factory.Services.GetRequiredService<MetricBatchWriter>().FlushAsync(CancellationToken.None);
        return (agent, socket);
    }

    private async Task<AlertEngineResult> EvaluateAsync(RegisteredAgent agent)
    {
        var companyId = await database.GetCompanyIdAsync(Company);
        return await _api.Factory.Services.GetRequiredService<AlertEngine>()
            .EvaluateCompanyAsync(companyId, CancellationToken.None, [agent.MachineId]);
    }

    private async Task<JsonElement> DetailAsync(RegisteredAgent agent)
    {
        var client = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/maquinas/{agent.MachineId}");
    }

    private static object Services(string spoolerState) => new
    {
        servicios = new object[]
        {
            new { nombre = "Spooler", nombre_visible = "Cola de impresión", estado = spoolerState, inicio = "automatico" },
            new { nombre = "edgeupdate", nombre_visible = "Actualizador de Edge", estado = "detenido", inicio = "automatico_retrasado" },
        },
    };

    [DatabaseFact]
    public async Task A_full_disk_opens_a_problem_with_advice_and_it_resolves_itself()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        await SendAckedAsync(socket, "volumenes", new { volumenes = new[] { new { unidad = "C:", etiqueta = "Sistema", total_gb = 476, libre_gb = 9 } } });
        (await EvaluateAsync(agent)).Opened.Should().Be(1);

        var detail = await DetailAsync(agent);
        detail.GetProperty("status").GetString().Should().Be("critico");
        detail.GetProperty("volumes")[0].GetProperty("drive").GetString().Should().Be("C:");
        var problem = detail.GetProperty("problems").EnumerateArray().Single();
        problem.GetProperty("message").GetString().Should().Contain("Disco C:");
        problem.GetProperty("advice").GetString().Should().Contain("Libera espacio");
        problem.GetProperty("source").GetString().Should().StartWith("Unidad C:");

        // Evaluar otra vez sin cambios no duplica la alerta.
        (await EvaluateAsync(agent)).Opened.Should().Be(0);

        await SendAckedAsync(socket, "volumenes", new { volumenes = new[] { new { unidad = "C:", etiqueta = "Sistema", total_gb = 476, libre_gb = 300 } } });
        (await EvaluateAsync(agent)).Resolved.Should().Be(1);

        detail = await DetailAsync(agent);
        detail.GetProperty("status").GetString().Should().Be("sano");
        detail.GetProperty("problems").GetArrayLength().Should().Be(0);
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.alertas WHERE maquina_id = @id AND estado = 'resuelta' AND resuelta IS NOT NULL",
            ("@id", agent.MachineId))).Should().Be(1);
    }

    [DatabaseFact]
    public async Task Only_a_service_that_was_running_and_stops_is_a_problem()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        // La primera vez llegan detenidos los que se detienen solos (edgeupdate): no es una caída.
        await SendAckedAsync(socket, "servicios", Services("en_ejecucion"));
        (await EvaluateAsync(agent)).Opened.Should().Be(0);

        await SendAckedAsync(socket, "servicios", Services("detenido"));
        (await EvaluateAsync(agent)).Opened.Should().Be(1);
        (await _api.ScalarAsync("SELECT codigo FROM dbo.alertas WHERE maquina_id = @id AND estado <> 'resuelta'", ("@id", agent.MachineId)))
            .Should().Be("servicio:Spooler");

        var client = _api.ClientWith(await _api.TokenForAsync(Company, "Administrador"));
        var services = await client.GetFromJsonAsync<JsonElement>($"/api/v1/maquinas/{agent.MachineId}/servicios");
        services[0].GetProperty("name").GetString().Should().Be("Spooler", "los caídos van primero");
        services[0].GetProperty("stoppedSince").ValueKind.Should().Be(JsonValueKind.String);

        await SendAckedAsync(socket, "servicios", Services("en_ejecucion"));
        (await EvaluateAsync(agent)).Resolved.Should().Be(1);
    }

    [DatabaseFact]
    public async Task Disk_health_and_processes_reach_the_machine_detail()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        await SendAckedAsync(socket, "inventario_hardware", new
        {
            ram_total_mb = 16384,
            componentes = new object[] { new { tipo = "disco", descripcion = "WDC WD10EZEX", capacidad = "1000 GB", estado_salud = "sano" } },
        });
        await SendAckedAsync(socket, "salud_discos", new
        {
            discos = new[] { new { modelo = "WDC WD10EZEX", salud = "critico", sectores_reasignados = 148, temperatura = 41 } },
        });
        await SendAckedAsync(socket, "procesos", new
        {
            total = 142,
            procesos = new[] { new { nombre = "chrome", pid = 10, cpu = 12.5, ram_mb = 800 } },
        });
        await EvaluateAsync(agent);

        var detail = await DetailAsync(agent);
        detail.GetProperty("ramTotalMb").GetInt32().Should().Be(16384);
        detail.GetProperty("processCount").GetInt32().Should().Be(142);
        var disk = detail.GetProperty("components").EnumerateArray().Single(c => c.GetProperty("type").GetString() == "disco");
        disk.GetProperty("health").GetString().Should().Be("critico");
        disk.GetProperty("detail").GetString().Should().Be("148 sectores dañados · 41 °C");
        detail.GetProperty("problems").EnumerateArray().Should()
            .Contain(p => p.GetProperty("message").GetString() == "Disco con sectores dañados");

        // Un inventario de hardware nuevo no borra la salud del disco que mandó SMART.
        await SendAckedAsync(socket, "inventario_hardware", new
        {
            componentes = new object[] { new { tipo = "disco", descripcion = "WDC WD10EZEX", estado_salud = "sano" } },
        });
        (await _api.ScalarAsync("SELECT estado_salud FROM dbo.componentes WHERE maquina_id = @id AND tipo = 'disco'", ("@id", agent.MachineId)))
            .Should().Be("critico");
    }
}

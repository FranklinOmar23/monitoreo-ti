using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MonitoreoTI.Api.Agents;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Infrastructure.Agents;
using NSubstitute;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class AgentTelemetryTests(TestDatabase database) : IAsyncLifetime
{
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

    private Task FlushMetricsAsync() => _api.Factory.Services.GetRequiredService<MetricBatchWriter>().FlushAsync(CancellationToken.None);

    private async Task<(RegisteredAgent Agent, WebSocket Socket)> ConnectAgentAsync(string company = "Grupo Comercial Caribe", string branch = "Santiago")
    {
        var agent = await _api.RegisterNewAgentAsync(company, branch);
        return (agent, await ConnectAndGreetAsync(_api, agent));
    }

    private async Task<List<MetricRow>> MetricsOfAsync(int machineId)
    {
        await using var connection = await database.OpenConnectionAsync(superAdmin: true);
        await using var command = new SqlCommand(
            "SELECT fecha_hora, cpu, ram, disco, temperatura FROM dbo.metricas WHERE maquina_id = @id ORDER BY fecha_hora", connection);
        command.Parameters.AddWithValue("@id", machineId);
        await using var reader = await command.ExecuteReaderAsync();

        var rows = new List<MetricRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new MetricRow(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4)));
        }
        return rows;
    }

    [DatabaseFact]
    public async Task Metrics_are_acknowledged_and_saved_in_a_batch()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-2);
        await SendAckedAsync(socket, "metricas", new { cpu = 12.5, ram = 40, disco = 71.25, temperatura = 55 }, t0);
        await SendAckedAsync(socket, "metricas", new { cpu = 15, ram = 41, disco = 71.25, temperatura = (double?)null }, t0.AddMinutes(1));

        (await MetricsOfAsync(agent.MachineId)).Should().BeEmpty("se guardan por lotes, no en cada mensaje");

        await FlushMetricsAsync();

        var rows = await MetricsOfAsync(agent.MachineId);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new MetricRow(rows[0].At, 12.5m, 40m, 71.25m, 55m));
        rows[0].At.Should().BeCloseTo(t0.UtcDateTime, TimeSpan.FromSeconds(1));
        rows[1].Temperature.Should().BeNull();
    }

    [DatabaseFact]
    public async Task Resent_messages_do_not_duplicate_rows()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);

        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 10, disco = 10 }, at);
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 10, disco = 10 }, at);
        await FlushMetricsAsync();
        // El agente no recibió el ack y reenvía en otro lote.
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 10, disco = 10 }, at);
        await FlushMetricsAsync();

        (await MetricsOfAsync(agent.MachineId)).Should().HaveCount(1);
    }

    [DatabaseFact]
    public async Task Out_of_range_values_are_dropped_and_untrusted_timestamps_use_the_server_clock()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;
        var now = DateTime.UtcNow;

        await SendAckedAsync(socket, "metricas", new { cpu = 150, ram = -5, disco = 50, temperatura = 400 }, now.AddMinutes(-10));
        // Una muestra de hace 3 días (cola local tras una caída larga) conserva su hora real.
        await SendAckedAsync(socket, "metricas", new { cpu = 1, ram = 1, disco = 1 }, now.AddDays(-3));
        // Relojes desajustados: del futuro o de hace más de 7 días, se usa la hora de recepción.
        await SendAckedAsync(socket, "metricas", new { cpu = 2, ram = 2, disco = 2 }, now.AddDays(1));
        await SendAckedAsync(socket, "metricas", new { cpu = 3, ram = 3, disco = 3 }, now.AddDays(-30));
        await FlushMetricsAsync();

        var rows = await MetricsOfAsync(agent.MachineId);
        rows.Should().Contain(r => r.Cpu == null && r.Ram == null && r.Disk == 50m && r.Temperature == null);
        rows.Should().Contain(r => r.Cpu == 1m && Math.Abs((r.At - now.AddDays(-3)).TotalMinutes) < 1);
        // Las dos caen en la hora de recepción (el mismo segundo) y cuentan como una sola muestra.
        rows.Where(r => r.Cpu is 2m or 3m).Should().ContainSingle()
            .Which.At.Should().BeCloseTo(now, TimeSpan.FromMinutes(2));
    }

    [DatabaseFact]
    public async Task The_machine_comes_from_the_connection_never_from_the_message()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;
        var victim = await _api.RegisterNewAgentAsync("Clínica San Rafael", "Santo Domingo");

        await SendAckedAsync(socket, "metricas", new { cpu = 99, ram = 99, disco = 99, maquina_id = victim.MachineId, empresa_id = 2, machineId = victim.MachineId });
        await FlushMetricsAsync();

        (await MetricsOfAsync(victim.MachineId)).Should().BeEmpty();
        (await MetricsOfAsync(agent.MachineId)).Should().ContainSingle(r => r.Cpu == 99m);
    }

    [DatabaseFact]
    public async Task Malformed_payloads_are_acknowledged_and_ignored_instead_of_blocking_the_agent_queue()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        await SendAckedAsync(socket, "metricas", "no es un objeto");
        await SendAckedAsync(socket, "inventario_software", new { programas = "tampoco" });
        await SendAckedAsync(socket, "interfaces_red", new { interfaces = new[] { new { nombre = "x", mac = "mac-invalida" } } });
        await FlushMetricsAsync();

        (await MetricsOfAsync(agent.MachineId)).Should().BeEmpty();
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.interfaces_red WHERE maquina_id = @id", ("@id", agent.MachineId))).Should().Be(0);
    }

    [DatabaseFact]
    public async Task Hardware_inventory_updates_the_machine_and_replaces_its_components()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        await SendAckedAsync(socket, "inventario_hardware", new
        {
            fabricante = "Dell Inc.",
            modelo = "OptiPlex 7090",
            serie = "ABC1234",
            tipo = "laptop",
            sistema_operativo = "Microsoft Windows 11 Pro 10.0.22631",
            componentes = new object[]
            {
                new { tipo = "cpu", descripcion = "Intel i5-11500", capacidad = "6 núcleos", estado_salud = "sano" },
                new { tipo = "ram", descripcion = "2 × 8 GB DDR4", capacidad = "16 GB", estado_salud = "desconocido" },
                new { tipo = "disco", descripcion = "Samsung SSD", capacidad = "477 GB", estado_salud = "algo-raro" },
                new { tipo = "", descripcion = "sin tipo: se descarta" },
            },
        });

        var machine = await _api.QueryMachineAsync(agent.Mac);
        (await _api.ScalarAsync("SELECT marca + '|' + modelo + '|' + serie + '|' + tipo FROM dbo.maquinas WHERE id = @id", ("@id", agent.MachineId)))
            .Should().Be("Dell Inc.|OptiPlex 7090|ABC1234|laptop");
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.componentes WHERE maquina_id = @id", ("@id", agent.MachineId))).Should().Be(3);
        (await _api.ScalarAsync("SELECT estado_salud FROM dbo.componentes WHERE maquina_id = @id AND tipo = 'disco'", ("@id", agent.MachineId)))
            .Should().Be("desconocido", "un estado inválido no rompe el CHECK de la base");

        // El siguiente inventario reemplaza al anterior por completo.
        await SendAckedAsync(socket, "inventario_hardware", new
        {
            tipo = "computadora",
            componentes = new object[] { new { tipo = "cpu", descripcion = "Intel i7", estado_salud = "sano" } },
        });
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.componentes WHERE maquina_id = @id", ("@id", agent.MachineId))).Should().Be(1);
        machine.Name.Should().NotBeNull();
    }

    [DatabaseFact]
    public async Task Software_inventory_replaces_the_list_and_cleans_the_data()
    {
        var (agent, socket) = await ConnectAgentAsync();
        using var _ = socket;

        await SendAckedAsync(socket, "inventario_software", new
        {
            programas = new object[]
            {
                new { nombre = "7-Zip 24.08 (x64)", version = "24.08", editor = "Igor Pavlov", fecha_instalacion = "2024-09-10" },
                new { nombre = new string('N', 400), version = "1", editor = "E", fecha_instalacion = "no-fecha" },
                new { nombre = "   " },
            },
        });

        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.software_instalado WHERE maquina_id = @id", ("@id", agent.MachineId))).Should().Be(2);
        (await _api.ScalarAsync("SELECT MAX(LEN(nombre)) FROM dbo.software_instalado WHERE maquina_id = @id", ("@id", agent.MachineId))).Should().Be(250);
        (await _api.ScalarAsync("SELECT fecha_instalacion FROM dbo.software_instalado WHERE maquina_id = @id AND version = '24.08'", ("@id", agent.MachineId)))
            .Should().Be(new DateTime(2024, 9, 10));

        await SendAckedAsync(socket, "inventario_software", new { programas = new object[] { new { nombre = "Solo este" } } });
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.software_instalado WHERE maquina_id = @id", ("@id", agent.MachineId))).Should().Be(1);
    }

    [DatabaseFact]
    public async Task Network_interfaces_are_synced_and_a_mac_moves_with_its_card()
    {
        var (first, firstSocket) = await ConnectAgentAsync();
        using var _ = firstSocket;
        var (second, secondSocket) = await ConnectAgentAsync();
        using var __ = secondSocket;
        var sharedMac = AgentTestApi.NewMac();

        await SendAckedAsync(firstSocket, "interfaces_red", new
        {
            interfaces = new object[]
            {
                new { nombre = "Ethernet", mac = first.Mac, ip = "10.1.1.5", tipo = "cableada", velocidad_mbps = 1000 },
                new { nombre = "Wi-Fi", mac = sharedMac.ToLowerInvariant(), ip = "10.1.1.6", tipo = "wifi", velocidad_mbps = 300 },
                new { nombre = "Basura", mac = "00:00:00:00:00:00" },
            },
        });
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.interfaces_red WHERE maquina_id = @id", ("@id", first.MachineId))).Should().Be(2);
        (await _api.ScalarAsync("SELECT ip FROM dbo.maquinas WHERE id = @id", ("@id", first.MachineId))).Should().Be("10.1.1.5", "la IP de la ficha sigue a la tarjeta principal");

        // La tarjeta Wi-Fi pasa a otro equipo (la MAC es única por empresa) y la primera máquina ya no la reporta.
        await SendAckedAsync(secondSocket, "interfaces_red", new
        {
            interfaces = new object[] { new { nombre = "Wi-Fi", mac = sharedMac, tipo = "wifi" } },
        });
        (await _api.ScalarAsync("SELECT maquina_id FROM dbo.interfaces_red WHERE mac = @m", ("@m", sharedMac))).Should().Be(second.MachineId);

        await SendAckedAsync(firstSocket, "interfaces_red", new
        {
            interfaces = new object[] { new { nombre = "Ethernet", mac = first.Mac, ip = "10.1.1.9", tipo = "cableada" } },
        });
        (await _api.ScalarAsync("SELECT COUNT(*) FROM dbo.interfaces_red WHERE maquina_id = @id", ("@id", first.MachineId))).Should().Be(1);
        (await _api.ScalarAsync("SELECT maquina_id FROM dbo.interfaces_red WHERE mac = @m", ("@m", sharedMac))).Should().Be(second.MachineId, "el agente 1 no se la lleva de vuelta");
    }

    [Fact]
    public async Task A_full_metric_queue_makes_the_handler_fail_so_the_message_is_not_acknowledged()
    {
        var sink = Substitute.For<IMetricSink>();
        sink.TryEnqueue(Arg.Any<MetricsSample>()).Returns(false);
        var handler = new MetricsHandler(sink, Substitute.For<MonitoreoTI.Application.Panel.IRealtimeNotifier>(), TimeProvider.System, NullLogger<MetricsHandler>.Instance);
        var message = new AgentEnvelope(1, "metricas", "id-1", DateTimeOffset.UtcNow, JsonDocument.Parse("""{"cpu":1}""").RootElement);

        var act = () => handler.HandleAsync(new AgentIdentity(1, 1, 1, false), message, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed record MetricRow(DateTime At, decimal? Cpu, decimal? Ram, decimal? Disk, decimal? Temperature);
}

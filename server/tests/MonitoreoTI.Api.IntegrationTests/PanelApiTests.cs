using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>
/// API de lectura del panel. Criterio de aceptación de la Fase 2: dos empresas no pueden ver datos entre sí,
/// ni listando ni pidiendo un recurso por su id.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PanelApiTests(TestDatabase database) : IAsyncLifetime
{
    private const string Caribe = "Grupo Comercial Caribe";
    private const string Rafael = "Clínica San Rafael";
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

    private async Task<int> MachineIdAsync(string company, string name) =>
        (int)(await _api.ScalarAsync(
            "SELECT m.id FROM dbo.maquinas m JOIN dbo.empresas e ON e.id = m.empresa_id WHERE e.nombre = @e AND m.nombre = @n",
            ("@e", company), ("@n", name)))!;

    private async Task<int> BranchIdAsync(string company, string name) =>
        (int)(await _api.ScalarAsync(
            "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = @n",
            ("@e", company), ("@n", name)))!;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<string> Names(JsonElement list) =>
        list.GetProperty("page").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!);

    // ---------------------------------------------------------------- aislamiento entre empresas

    [DatabaseFact]
    public async Task Each_company_lists_only_its_own_machines_and_branches()
    {
        using var caribe = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        using var rafael = _api.ClientWith(await _api.TokenForAsync(Rafael, "Administrador"));

        var caribeMachines = await JsonAsync(await caribe.GetAsync("/api/v1/maquinas"));
        var rafaelMachines = await JsonAsync(await rafael.GetAsync("/api/v1/maquinas"));

        Names(caribeMachines).Should().Contain(["CAJA-01", "RRHH-02", "SERVIDOR-01"]).And.NotContain(n => n.StartsWith("PC-RAFAEL"));
        Names(rafaelMachines).Should().BeEquivalentTo("CONTA-03");
        caribeMachines.GetProperty("page").GetProperty("total").GetInt32().Should().BeGreaterThan(1);

        var caribeBranches = await JsonAsync(await caribe.GetAsync("/api/v1/sucursales"));
        var rafaelBranches = await JsonAsync(await rafael.GetAsync("/api/v1/sucursales"));
        caribeBranches.GetArrayLength().Should().Be(5);
        rafaelBranches.GetArrayLength().Should().Be(1);
    }

    [DatabaseFact]
    public async Task Another_company_machine_is_not_found_even_by_id_on_every_endpoint()
    {
        using var caribe = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var foreign = await MachineIdAsync(Rafael, "CONTA-03");
        var foreignBranch = await BranchIdAsync(Rafael, "Santo Domingo");

        foreach (var path in new[]
        {
            $"/api/v1/maquinas/{foreign}", $"/api/v1/maquinas/{foreign}/metricas?rango=24h", $"/api/v1/maquinas/{foreign}/software",
            $"/api/v1/maquinas/{foreign}/interfaces", $"/api/v1/maquinas/{foreign}/eventos", $"/api/v1/maquinas/{foreign}/procesos",
            $"/api/v1/maquinas/{foreign}/historial", $"/api/v1/sucursales/{foreignBranch}",
        })
        {
            (await caribe.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }

        // Su propia CONTA-03 (mismo nombre, otra empresa) sí la ve.
        var own = await MachineIdAsync(Caribe, "CONTA-03");
        (await caribe.GetAsync($"/api/v1/maquinas/{own}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [DatabaseFact]
    public async Task Dashboard_counts_only_the_company_of_the_session()
    {
        using var rafael = _api.ClientWith(await _api.TokenForAsync(Rafael, "Administrador"));

        var dashboard = await JsonAsync(await rafael.GetAsync("/api/v1/dashboard"));

        dashboard.GetProperty("kpis").GetProperty("counts").GetProperty("total").GetInt32().Should().Be(1);
        dashboard.GetProperty("branches").GetArrayLength().Should().Be(1);
        dashboard.GetProperty("recentAlerts").EnumerateArray()
            .Should().OnlyContain(a => a.GetProperty("machineName").GetString() == "CONTA-03");
    }

    // ---------------------------------------------------------------- permisos

    [DatabaseFact]
    public async Task Endpoints_require_a_session_and_the_right_permission()
    {
        (await _api.Client.GetAsync("/api/v1/maquinas")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Empleado: equipos.ver y tickets.crear, sin dashboard.ver.
        using var employee = _api.ClientWith(await _api.TokenForAsync(Caribe, "Empleado"));
        (await employee.GetAsync("/api/v1/maquinas")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await employee.GetAsync("/api/v1/dashboard")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------- alcance por sucursal

    [DatabaseFact]
    public async Task A_technician_only_sees_the_assigned_branches()
    {
        using var tech = _api.ClientWith(await _api.TokenForAsync(Caribe, "Tecnico", "Santiago"));

        var machines = await JsonAsync(await tech.GetAsync("/api/v1/maquinas"));
        Names(machines).Should().BeEquivalentTo("CAJA-01");

        var outside = await MachineIdAsync(Caribe, "CONTA-03");
        (await tech.GetAsync($"/api/v1/maquinas/{outside}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var branches = await JsonAsync(await tech.GetAsync("/api/v1/sucursales"));
        branches.EnumerateArray().Select(b => b.GetProperty("name").GetString()).Should().BeEquivalentTo("Santiago");
    }

    [DatabaseFact]
    public async Task An_administrator_sees_every_branch_even_with_assignments()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador", "Santiago"));

        var machines = await JsonAsync(await admin.GetAsync("/api/v1/maquinas"));

        Names(machines).Should().Contain(["CAJA-01", "CONTA-03"]);
    }

    // ---------------------------------------------------------------- lista, ficha y métricas

    [DatabaseFact]
    public async Task The_machine_list_filters_paginates_and_counts_by_status()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));

        var critical = await JsonAsync(await admin.GetAsync("/api/v1/maquinas?estado=critico"));
        critical.GetProperty("page").GetProperty("items").EnumerateArray()
            .Should().OnlyContain(i => i.GetProperty("status").GetString() == "critico");
        critical.GetProperty("counts").GetProperty("total").GetInt32()
            .Should().BeGreaterThan(critical.GetProperty("page").GetProperty("total").GetInt32(), "los chips cuentan sin el filtro de estado");

        var search = await JsonAsync(await admin.GetAsync("/api/v1/maquinas?q=CAJA"));
        Names(search).Should().BeEquivalentTo("CAJA-01");

        var page = await JsonAsync(await admin.GetAsync("/api/v1/maquinas?pagina=2&tamano=2"));
        page.GetProperty("page").GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("page").GetProperty("page").GetInt32().Should().Be(2);

        var withMetrics = await JsonAsync(await admin.GetAsync("/api/v1/maquinas?q=SERVIDOR-01"));
        withMetrics.GetProperty("page").GetProperty("items")[0].GetProperty("metrics").GetProperty("cpu").ValueKind
            .Should().Be(JsonValueKind.Number);
    }

    [DatabaseFact]
    public async Task The_machine_detail_brings_components_problems_and_metric_ranges()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var id = await MachineIdAsync(Caribe, "CAJA-01");

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{id}"));
        detail.GetProperty("name").GetString().Should().Be("CAJA-01");
        detail.GetProperty("branchName").GetString().Should().Be("Santiago");
        detail.GetProperty("components").GetArrayLength().Should().BeGreaterThan(0);
        detail.GetProperty("problems").EnumerateArray().Should().Contain(p => p.GetProperty("message").GetString()!.Contains("RAM"));

        var hour = await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{id}/metricas?rango=1h"));
        var day = await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{id}/metricas?rango=24h"));
        hour.GetArrayLength().Should().BeInRange(55, 61, "el seed tiene una muestra por minuto");
        day.GetArrayLength().Should().BeInRange(20, 26, "24 h se agrupa cada 5 minutos (2 h de datos en el seed)");

        (await admin.GetAsync($"/api/v1/maquinas/{id}/metricas?rango=3d")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{id}/software"))).GetArrayLength().Should().BeGreaterThan(0);
        (await JsonAsync(await admin.GetAsync($"/api/v1/maquinas/{id}/interfaces"))).GetArrayLength().Should().BeGreaterThan(0);
    }

    [DatabaseFact]
    public async Task The_dashboard_has_seven_days_of_fleet_health()
    {
        using var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));

        var dashboard = await JsonAsync(await admin.GetAsync("/api/v1/dashboard"));

        dashboard.GetProperty("fleetHealth").GetArrayLength().Should().Be(7);
        dashboard.GetProperty("branches").GetArrayLength().Should().Be(5);
        dashboard.GetProperty("topProblems").GetArrayLength().Should().BeGreaterThan(0);
    }

    // ---------------------------------------------------------------- tiempo real

    private HubConnection Hub(string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_api.Factory.Server.BaseAddress, "hubs/tiempo-real"), options =>
        {
            options.HttpMessageHandlerFactory = _ => _api.Factory.Server.CreateHandler();
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    [DatabaseFact]
    public async Task Open_machine_pages_receive_live_metrics_and_status_changes()
    {
        var agent = await _api.RegisterNewAgentAsync(Caribe, "Santiago");
        await using var hub = Hub(await _api.TokenForAsync(Caribe, "Administrador"));
        var metrics = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("metricasNuevas", m => metrics.TrySetResult(m));
        hub.On<JsonElement>("maquinaEstadoCambio", s => status.TrySetResult(s));
        await hub.StartAsync();
        await hub.InvokeAsync("SuscribirMaquina", agent.MachineId);

        // El agente se conecta (desconectado → sano) y manda una métrica.
        using var socket = await ConnectAndGreetAsync(_api, agent);
        await SendAckedAsync(socket, "metricas", new { cpu = 42.5, ram = 61, disco = 70 });

        var change = await status.Task.WaitAsync(TimeSpan.FromSeconds(10));
        change.GetProperty("maquinaId").GetInt32().Should().Be(agent.MachineId);
        change.GetProperty("estado").GetString().Should().Be("sano");

        var metric = await metrics.Task.WaitAsync(TimeSpan.FromSeconds(10));
        metric.GetProperty("maquinaId").GetInt32().Should().Be(agent.MachineId);
        metric.GetProperty("cpu").GetDecimal().Should().Be(42.5m);
    }

    [DatabaseFact]
    public async Task Nobody_can_subscribe_to_another_company_machine()
    {
        var foreign = await MachineIdAsync(Rafael, "CONTA-03");
        await using var hub = Hub(await _api.TokenForAsync(Caribe, "Administrador"));
        await hub.StartAsync();

        var act = () => hub.InvokeAsync("SuscribirMaquina", foreign);

        await act.Should().ThrowAsync<HubException>();
    }

    [DatabaseFact]
    public async Task The_hub_requires_a_session()
    {
        await using var hub = Hub("token-invalido");

        var act = () => hub.StartAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}

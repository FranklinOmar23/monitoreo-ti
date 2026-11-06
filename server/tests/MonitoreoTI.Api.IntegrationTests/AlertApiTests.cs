using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Infrastructure.Agents;
using MonitoreoTI.Infrastructure.Health;
using static MonitoreoTI.Api.IntegrationTests.Infrastructure.AgentSocket;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Alertas (W-20, W-21) y reglas (W-22, W-23) por el API del panel.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class AlertApiTests(TestDatabase database) : IAsyncLifetime
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

    /// <summary>Un equipo de Santiago con el disco C: al <paramref name="usedPercent"/> %, ya evaluado por el motor.</summary>
    private async Task<RegisteredAgent> MachineWithDiskAsync(decimal usedPercent)
    {
        var agent = await _api.RegisterNewAgentAsync(Company, "Santiago");
        using var socket = await ConnectAndGreetAsync(_api, agent);
        await SendAckedAsync(socket, "metricas", new { cpu = 10, ram = 30, disco = usedPercent });
        await _api.Factory.Services.GetRequiredService<MetricBatchWriter>().FlushAsync(CancellationToken.None);
        await SendAckedAsync(socket, "volumenes", new { volumenes = new[] { new { unidad = "C:", total_gb = 100, libre_gb = 100 - usedPercent } } });
        await EvaluateAsync(agent);
        return agent;
    }

    private async Task EvaluateAsync(RegisteredAgent agent) =>
        await _api.Factory.Services.GetRequiredService<AlertEngine>()
            .EvaluateCompanyAsync(await database.GetCompanyIdAsync(Company), CancellationToken.None, [agent.MachineId]);

    private async Task<HttpClient> ClientAsync(string role = "Administrador", params string[] branches) =>
        _api.ClientWith(await _api.TokenForAsync(Company, role, branches));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static long AlertIdOf(JsonElement list, int machineId) =>
        list.GetProperty("data").GetProperty("data").EnumerateArray()
            .Single(a => a.GetProperty("machineId").GetInt32() == machineId).GetProperty("id").GetInt64();

    [DatabaseFact]
    public async Task An_alert_is_listed_attended_and_resolved_with_its_history()
    {
        var agent = await MachineWithDiskAsync(97);
        var admin = await ClientAsync();

        var list = await JsonAsync(await admin.GetAsync($"/api/v1/alertas?maquina={agent.MachineId}"));
        var id = AlertIdOf(list, agent.MachineId);
        list.GetProperty("counts").GetProperty("open").GetInt32().Should().BeGreaterThan(0);

        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/alertas/{id}"));
        detail.GetProperty("level").GetString().Should().Be("critico");
        detail.GetProperty("automatic").GetBoolean().Should().BeTrue();
        detail.GetProperty("ruleName").GetString().Should().Be("Disco usado");
        detail.GetProperty("branchName").GetString().Should().Be("Santiago");
        detail.GetProperty("history")[0].GetProperty("action").GetString().Should().Be("Detectada por el sistema");

        (await admin.PostAsJsonAsync($"/api/v1/alertas/{id}/atender", new { nota = (string?)null })).EnsureSuccessStatusCode();
        detail = await JsonAsync(await admin.GetAsync($"/api/v1/alertas/{id}"));
        detail.GetProperty("status").GetString().Should().Be("en_proceso");
        detail.GetProperty("handledBy").GetString().Should().Be("Usuario de prueba");

        (await admin.PostAsJsonAsync($"/api/v1/alertas/{id}/resolver", new { nota = "Se liberaron 40 GB." })).EnsureSuccessStatusCode();
        detail = await JsonAsync(await admin.GetAsync($"/api/v1/alertas/{id}"));
        detail.GetProperty("status").GetString().Should().Be("resuelta");
        detail.GetProperty("history").EnumerateArray().Should()
            .Contain(h => h.GetProperty("action").GetString() == "Resuelta" && h.GetProperty("note").GetString() == "Se liberaron 40 GB.");

        (await admin.PostAsJsonAsync($"/api/v1/alertas/{id}/resolver", new { nota = (string?)null })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Sigue lleno: el motor abre una alerta nueva y la detalle cuenta la repetición.
        await EvaluateAsync(agent);
        list = await JsonAsync(await admin.GetAsync($"/api/v1/alertas?maquina={agent.MachineId}"));
        var reopened = AlertIdOf(list, agent.MachineId);
        reopened.Should().NotBe(id);
        (await JsonAsync(await admin.GetAsync($"/api/v1/alertas/{reopened}"))).GetProperty("recurrences").GetInt32().Should().Be(1);
    }

    [DatabaseFact]
    public async Task Roles_and_branches_limit_who_sees_and_who_acts()
    {
        var agent = await MachineWithDiskAsync(97);
        var admin = await ClientAsync();
        var id = AlertIdOf(await JsonAsync(await admin.GetAsync($"/api/v1/alertas?maquina={agent.MachineId}")), agent.MachineId);

        var employee = await ClientAsync("Empleado");
        (await employee.GetAsync("/api/v1/alertas")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var supervisor = await ClientAsync("Supervisor");
        (await supervisor.GetAsync($"/api/v1/alertas/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await supervisor.PostAsJsonAsync($"/api/v1/alertas/{id}/atender", new { nota = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var otherBranch = await ClientAsync("Tecnico", "Santo Domingo");
        (await otherBranch.GetAsync($"/api/v1/alertas/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherBranch.PostAsJsonAsync($"/api/v1/alertas/{id}/atender", new { nota = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        var list = await JsonAsync(await otherBranch.GetAsync("/api/v1/alertas?estado=todas&limit=100"));
        list.GetProperty("data").GetProperty("data").EnumerateArray()
            .Should().OnlyContain(a => a.GetProperty("branchName").GetString() == "Santo Domingo");
    }

    [DatabaseFact]
    public async Task A_branch_rule_overrides_the_company_rule_for_its_machines()
    {
        var admin = await ClientAsync();
        var rules = await JsonAsync(await admin.GetAsync("/api/v1/reglas-alerta"));
        rules.GetArrayLength().Should().Be(12);
        var disk = rules.EnumerateArray().Single(r => r.GetProperty("metric").GetString() == "disco");
        disk.GetProperty("warning").GetDecimal().Should().Be(80);

        var branchId = (int)(await _api.ScalarAsync(
            "SELECT s.id FROM dbo.sucursales s JOIN dbo.empresas e ON e.id = s.empresa_id WHERE e.nombre = @e AND s.nombre = 'Santiago'",
            ("@e", Company)))!;
        var saved = await admin.PutAsJsonAsync("/api/v1/reglas-alerta",
            new { metrica = "disco", sucursalId = branchId, umbralAdvertencia = 50, umbralCritico = 60, duracionMin = 0, activa = true });
        var ruleId = (await JsonAsync(saved)).GetProperty("id").GetInt32();

        // 55 % no alcanza la regla de la empresa (80 %) pero sí la de Santiago (50 %).
        var agent = await MachineWithDiskAsync(55);
        var alerts = await JsonAsync(await admin.GetAsync($"/api/v1/alertas?maquina={agent.MachineId}"));
        var alert = alerts.GetProperty("data").GetProperty("data").EnumerateArray().Single();
        alert.GetProperty("level").GetString().Should().Be("advertencia");

        rules = await JsonAsync(await admin.GetAsync("/api/v1/reglas-alerta"));
        rules.EnumerateArray().Single(r => r.GetProperty("metric").GetString() == "disco")
            .GetProperty("overrides").EnumerateArray().Should().ContainSingle(o => o.GetProperty("branchName").GetString() == "Santiago");

        (await admin.DeleteAsync($"/api/v1/reglas-alerta/{ruleId}")).EnsureSuccessStatusCode();
        await EvaluateAsync(agent);
        (await JsonAsync(await admin.GetAsync($"/api/v1/alertas?maquina={agent.MachineId}")))
            .GetProperty("data").GetProperty("total").GetInt32().Should().Be(0, "sin el ajuste vuelve a la regla de la empresa y se resuelve sola");
    }

    [DatabaseFact]
    public async Task Rules_are_validated_and_branch_users_cannot_change_company_rules()
    {
        var admin = await ClientAsync();
        var backwards = await admin.PutAsJsonAsync("/api/v1/reglas-alerta",
            new { metrica = "disco", sucursalId = (int?)null, umbralAdvertencia = 95, umbralCritico = 80, duracionMin = 0, activa = true });
        backwards.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await backwards.Content.ReadAsStringAsync()).Should().Contain("mayor que el de advertencia");

        var unknown = await admin.PutAsJsonAsync("/api/v1/reglas-alerta",
            new { metrica = "cafe", sucursalId = (int?)null, umbralAdvertencia = 1, umbralCritico = 2, duracionMin = 0, activa = true });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var branchTech = await ClientAsync("Tecnico", "Santiago");
        var companyWide = await branchTech.PutAsJsonAsync("/api/v1/reglas-alerta",
            new { metrica = "disco", sucursalId = (int?)null, umbralAdvertencia = 70, umbralCritico = 90, duracionMin = 0, activa = true });
        companyWide.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _api.ScalarAsync(
            "SELECT umbral_advertencia FROM dbo.reglas_alerta r JOIN dbo.empresas e ON e.id = r.empresa_id WHERE e.nombre = @e AND r.metrica = 'disco' AND r.sucursal_id IS NULL",
            ("@e", Company))).Should().Be(80m);
    }
}

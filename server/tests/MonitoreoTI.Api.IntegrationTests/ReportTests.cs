using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests;

/// <summary>Reportes (W-35): alcance por sucursal, periodo válido y reportes avanzados solo con el módulo del plan.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReportTests(TestDatabase database) : IAsyncLifetime
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

    [DatabaseFact]
    public async Task A_basic_plan_gets_the_basic_reports_but_not_the_advanced_ones_even_calling_the_api()
    {
        var supervisor = _api.ClientWith(await _api.TokenForAsync(Rafael, "Supervisor"));

        var info = await supervisor.GetFromJsonAsync<JsonElement>("/api/v1/reportes");
        info.GetProperty("advanced").GetBoolean().Should().BeFalse();
        (await supervisor.GetAsync("/api/v1/reportes/inventario")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await supervisor.GetAsync("/api/v1/reportes/alertas")).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var report in new[] { "rendimiento", "software", "tickets", "remoto" })
        {
            var response = await supervisor.GetAsync($"/api/v1/reportes/{report}");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, report);
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("codigo").GetString().Should().Be("modulo_no_incluido");
        }

        var enterprise = _api.ClientWith(await _api.TokenForAsync(Caribe, "Supervisor"));
        (await enterprise.GetFromJsonAsync<JsonElement>("/api/v1/reportes")).GetProperty("advanced").GetBoolean().Should().BeTrue();
        foreach (var report in new[] { "rendimiento", "software", "software?buscar=office", "tickets", "remoto", "disponibilidad", "discos-seguridad" })
            (await enterprise.GetAsync($"/api/v1/reportes/{report}")).StatusCode.Should().Be(HttpStatusCode.OK, report);
    }

    [DatabaseFact]
    public async Task The_inventory_only_has_the_branches_the_user_sees()
    {
        var limited = _api.ClientWith(await _api.TokenForAsync(Caribe, "Tecnico", "Santiago"));
        var report = await limited.GetFromJsonAsync<JsonElement>("/api/v1/reportes/inventario");
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().NotBeEmpty();
        rows.Select(r => r.GetProperty("branchName").GetString()).Should().OnlyContain(b => b == "Santiago");
        report.GetProperty("total").GetInt32().Should().Be(rows.Count);

        var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/v1/reportes/inventario");
        all.GetProperty("total").GetInt32().Should().BeGreaterThan(rows.Count);
    }

    [DatabaseFact]
    public async Task Full_disks_and_machines_without_working_antivirus_show_up()
    {
        var agent = await _api.RegisterNewAgentAsync();
        var companyId = await database.GetCompanyIdAsync(Caribe);
        await _api.ExecuteAsync("""
            INSERT INTO dbo.volumenes (empresa_id, maquina_id, unidad, total_gb, libre_gb) VALUES (@e, @m, 'C:', 200, 8);
            INSERT INTO dbo.componentes (empresa_id, maquina_id, tipo, descripcion, estado_salud, detalle)
            VALUES (@e, @m, 'antivirus', N'Antivirus X', 'critico', N'Desactivado'),
                   (@e, @m, 'firewall', N'Firewall de Windows', 'advertencia', N'Desactivado'),
                   (@e, @m, 'disco', N'Disco 0', 'critico', N'148 sectores dañados');
            """, ("@e", companyId), ("@m", agent.MachineId));

        var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        var report = await admin.GetFromJsonAsync<JsonElement>("/api/v1/reportes/discos-seguridad");

        var disk = report.GetProperty("lowDisks").EnumerateArray().Single(d => d.GetProperty("machineId").GetInt32() == agent.MachineId);
        disk.GetProperty("freePercent").GetDecimal().Should().Be(4m);
        report.GetProperty("diskHealth").EnumerateArray()
            .Should().Contain(d => d.GetProperty("machineId").GetInt32() == agent.MachineId && d.GetProperty("health").GetString() == "critico");
        report.GetProperty("security").EnumerateArray()
            .Where(s => s.GetProperty("machineId").GetInt32() == agent.MachineId)
            .Select(s => s.GetProperty("issue").GetString())
            .Should().BeEquivalentTo("antivirus_desactivado", "firewall_desactivado");
    }

    [DatabaseFact]
    public async Task The_period_must_be_valid()
    {
        var admin = _api.ClientWith(await _api.TokenForAsync(Caribe, "Administrador"));
        (await admin.GetAsync("/api/v1/reportes/alertas?desde=2026-10-10&hasta=2026-10-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/reportes/alertas?desde=2025-01-01&hasta=2026-10-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var alerts = await admin.GetFromJsonAsync<JsonElement>("/api/v1/reportes/alertas?desde=2026-01-01&hasta=2026-12-31");
        alerts.GetProperty("total").GetInt32().Should().Be(
            alerts.GetProperty("critical").GetInt32() + alerts.GetProperty("warning").GetInt32());
        var availability = await admin.GetFromJsonAsync<JsonElement>("/api/v1/reportes/disponibilidad");
        availability.GetProperty("machines").EnumerateArray()
            .Should().OnlyContain(m => m.GetProperty("percent").GetDecimal() >= 0 && m.GetProperty("percent").GetDecimal() <= 100);
    }
}

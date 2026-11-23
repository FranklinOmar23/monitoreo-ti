using FluentAssertions;
using MonitoreoTI.Application.Reports;

namespace MonitoreoTI.Application.Tests;

/// <summary>Decisión 92: periodo anterior, tramos del mapa de calor y "requiere atención" del resumen de reportes.</summary>
public class ReportRulesTests
{
    private static readonly ReportFilter Month = new(new DateOnly(2026, 10, 24), new DateOnly(2026, 11, 22));

    [Fact]
    public void The_previous_period_has_the_same_length_and_ends_the_day_before()
    {
        var previous = ReportRules.Previous(Month with { BranchId = 3 });
        previous.Should().Be(new ReportFilter(new DateOnly(2026, 9, 24), new DateOnly(2026, 10, 23), 3));
        ReportRules.Days(previous).Should().Be(30);
    }

    [Fact]
    public void Up_to_92_days_the_heatmap_goes_by_day_and_then_by_week()
    {
        ReportRules.Buckets(Month).Should().HaveCount(30);
        var year = new ReportFilter(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        ReportRules.BucketDays(year).Should().Be(7);
        var buckets = ReportRules.Buckets(year);
        buckets.Should().HaveCount(53);
        buckets[1].Should().Be(new DateOnly(2026, 1, 8));
    }

    [Fact]
    public void A_cell_counts_only_the_hours_the_machine_existed_and_that_already_passed()
    {
        var day = new DateTime(2026, 11, 20);
        ReportRules.Cell(day, day.AddDays(1), day.AddDays(-5), day.AddDays(3), 24).Should().Be(100);
        ReportRules.Cell(day, day.AddDays(1), day.AddDays(-5), day.AddDays(3), 18).Should().Be(75);
        // Dado de alta al mediodía: solo cuentan 12 horas.
        ReportRules.Cell(day, day.AddDays(1), day.AddHours(12), day.AddDays(3), 12).Should().Be(100);
        // Hoy a las 6: van 6 horas.
        ReportRules.Cell(day, day.AddDays(1), day.AddDays(-5), day.AddHours(6), 3).Should().Be(50);
        // Aún no existía o el día no ha llegado.
        ReportRules.Cell(day, day.AddDays(1), day.AddDays(2), day.AddDays(3), 0).Should().BeNull();
        ReportRules.Cell(day, day.AddDays(1), day.AddDays(-5), day.AddDays(-1), 0).Should().BeNull();
    }

    [Fact]
    public void The_fleet_average_skips_the_gaps()
    {
        IReadOnlyList<IReadOnlyList<decimal?>> rows = [[100, null, 50], [80, null, null]];
        ReportRules.ColumnAverages(rows, 3).Should().Equal(90, null, 50);
    }

    [Fact]
    public void A_daily_series_fills_the_missing_days()
    {
        var week = new ReportFilter(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 3));
        var series = ReportRules.Daily(week, [new DailyAlerts(new DateOnly(2026, 11, 2), 1, 4)], d => d.Date, d => d.Critical + d.Warning);
        series.Should().Equal(0, 5, 0);
    }

    [Fact]
    public void Critical_items_come_first_and_each_one_names_up_to_five_machines()
    {
        var disks = new DiskSecurityReport(
            [new LowDiskRow(1, "PC-01", "Santiago", "C:", 200, 20, 10)],
            [],
            [new SecurityRow(2, "PC-02", "Santiago", "sin_antivirus", null), new SecurityRow(2, "PC-02", "Santiago", "firewall_desactivado", null)]);
        var machines = Enumerable.Range(10, 7)
            .Select(i => new AvailabilityRow(i, $"PC-{i}", "Santiago", 20, 24, 83, null, []))
            .ToList();
        var availability = new AvailabilityReport(83, [], machines, [], 1, []);
        var alerts = new AlertReport(0, 0, 0, 0, null, [], [], [], []);

        var items = ReportRules.Attention(new AttentionInput(disks, availability, alerts));

        items.Select(i => i.Code).Should().Equal("sin_proteccion", "baja_disponibilidad", "disco_lleno");
        items[0].Should().BeEquivalentTo(new { Level = "critico", Count = 1, Report = "discos-seguridad" });
        items[1].Count.Should().Be(7);
        items[1].Machines.Should().HaveCount(ReportRules.SampleMachines);
        items[1].Level.Should().Be("advertencia");
        items[2].Level.Should().Be("advertencia");
    }

    [Fact]
    public void Advanced_items_only_appear_with_the_advanced_reports()
    {
        var disks = new DiskSecurityReport([], [], []);
        var availability = new AvailabilityReport(100, [], [], [], 1, []);
        var alerts = new AlertReport(3, 1, 2, 2, null, [], [], [], [new AlertMachine(5, "PC-05", "Santiago", 3, 1)]);
        var performance = new PerformanceReport([], [new PerformanceRow(5, "PC-05", "Santiago", 20, 40, 90, 99, 50, 24, ["ampliar_ram"])]);
        var tickets = new TicketReport(0, 0, 0, null, [], [], new MaintenanceSummary(0, 0, 2));

        ReportRules.Attention(new AttentionInput(disks, availability, alerts)).Select(i => i.Code)
            .Should().Equal("alertas_abiertas");
        ReportRules.Attention(new AttentionInput(disks, availability, alerts, performance, tickets)).Select(i => i.Code)
            .Should().BeEquivalentTo("alertas_abiertas", "ampliar_ram", "mantenimientos_vencidos");
    }

    [Fact]
    public void Branches_with_more_alerts_come_first()
    {
        var availability = new AvailabilityReport(90, [],
        [
            new AvailabilityRow(1, "A", "Santiago", 24, 24, 100, null, []),
            new AvailabilityRow(2, "B", "Santiago", 12, 24, 50, null, []),
            new AvailabilityRow(3, "C", "La Romana", 24, 24, 100, null, []),
        ], [], 1, []);
        var alerts = new AlertReport(4, 0, 4, 0, null, [], [], [new CountItem("La Romana", 4)], []);

        ReportRules.Branches(availability, alerts).Should().Equal(
            new BranchSummary("La Romana", 1, 100, 4),
            new BranchSummary("Santiago", 2, 75, 0));
    }
}

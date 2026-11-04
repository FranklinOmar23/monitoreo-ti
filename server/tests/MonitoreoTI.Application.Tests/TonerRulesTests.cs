using FluentAssertions;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Application.Tests;

public class TonerRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static MachineSnapshot Printer(params ComponentSnapshot[] supplies) =>
        new(1, "IMP-01", false, Now, [], [], [], [], supplies);

    private static ComponentSnapshot Toner(string name, string? capacity) => new("toner", name, "desconocido", null, capacity);

    [Theory]
    [InlineData("25 %", null)]
    [InlineData("15 %", FindingLevel.Warning)]
    [InlineData("3 %", FindingLevel.Critical)]
    public void Toner_levels_follow_the_rule(string capacity, FindingLevel? expected)
    {
        var findings = HealthRules.Evaluate(Printer(Toner("Tóner negro", capacity)), new RuleSet(), Now);
        if (expected is null)
            findings.Should().BeEmpty();
        else
            findings.Single().Should().Match<Finding>(f => f.Level == expected && f.Code == "toner:Tóner negro" && f.Metric == "toner");
    }

    [Fact]
    public void Supplies_without_a_measurable_level_do_not_alert() =>
        HealthRules.Evaluate(Printer(Toner("Tambor", null)), new RuleSet(), Now).Should().BeEmpty();

    [Fact]
    public void A_company_can_turn_the_rule_off() =>
        HealthRules.Evaluate(Printer(Toner("Tóner", "1 %")), new RuleSet(new Dictionary<string, RuleThreshold> { ["toner"] = new(20, 5, 0, false) }), Now)
            .Should().BeEmpty();

    [Theory]
    [InlineData(30, 100, 30)]
    [InlineData(1, 3, 33)]
    [InlineData(-3, 100, null)]
    [InlineData(50, 0, null)]
    public void Supply_percent(int level, int max, int? expected) => PrinterSupplies.Percent(level, max).Should().Be(expected);

    [Fact]
    public void Waste_receptacles_are_recognized() => PrinterSupplies.IsWasteReceptacle(4).Should().BeTrue();

    [Theory]
    [InlineData("2026-10-28", null, "vencido")]
    [InlineData("2026-10-30", null, "programado")]
    [InlineData("2026-10-28", "2026-10-29", "realizado")]
    public void Maintenance_status(string scheduled, string? done, string expected) =>
        MaintenanceRules.Status(DateOnly.Parse(scheduled), done is null ? null : DateOnly.Parse(done), new DateOnly(2026, 10, 30)).Should().Be(expected);
}

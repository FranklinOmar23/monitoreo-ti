using FluentAssertions;
using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Application.Tests;

/// <summary>Decisión 85: qué agentes entienden "reportar_ahora".</summary>
public class ReportNowRulesTests
{
    [Theory]
    [InlineData("0.4.2", true)]
    [InlineData("0.5.0", true)]
    [InlineData("0.4.1", false)]
    [InlineData("0.2.1-clasico", true)]
    [InlineData("0.2.0-clasico", false)]
    [InlineData(null, false)]
    [InlineData("basura", false)]
    public void Only_recent_agents_report_on_demand(string? version, bool expected) =>
        ReportNowRules.Supports(version).Should().Be(expected);
}

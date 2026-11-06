using FluentAssertions;
using MonitoreoTI.Application.Remote;
using Xunit;

namespace MonitoreoTI.Application.Tests;

/// <summary>Decisión 84: el agente clásico abre sesiones remotas desde la 0.2.0.</summary>
public class ClassicSessionRulesTests
{
    [Theory]
    [InlineData("0.1.0-clasico", true)]
    [InlineData("0.1.9-clasico", true)]
    [InlineData("basura-clasico", true)]
    [InlineData("0.2.0-clasico", false)]
    [InlineData("0.3.1-clasico", false)]
    [InlineData("1.0.0-clasico", false)]
    [InlineData("0.4.0", false)]
    [InlineData(null, false)]
    public void Classic_agents_before_0_2_have_no_sessions(string? version, bool refused) =>
        SessionRules.IsClassicWithoutSessions(version).Should().Be(refused);
}

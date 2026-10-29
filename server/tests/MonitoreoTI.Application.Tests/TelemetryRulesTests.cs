using FluentAssertions;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Application.Tests;

public class TelemetryRulesTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(55.555, 55.56)]
    [InlineData(100, 100)]
    public void Valid_percentages_are_kept(double input, double expected) =>
        TelemetryRules.Percent((decimal)input).Should().Be((decimal)expected);

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(5000)]
    public void Impossible_percentages_become_null(double input) =>
        TelemetryRules.Percent((decimal)input).Should().BeNull();

    [Fact]
    public void Null_stays_null() => TelemetryRules.Percent(null).Should().BeNull();

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(150)]
    public void Impossible_temperatures_become_null(double input) =>
        TelemetryRules.Celsius((decimal)input).Should().BeNull();

    [Fact]
    public void Plausible_temperature_is_kept() => TelemetryRules.Celsius(64.5m).Should().Be(64.5m);

    [Fact]
    public void Sample_time_trusts_the_agent_only_within_a_credible_window()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        TelemetryRules.SampleTime(now.AddHours(-5), now).Should().Be(now.AddHours(-5));
        TelemetryRules.SampleTime(now.AddMinutes(3), now).Should().Be(now.AddMinutes(3));
        TelemetryRules.SampleTime(now.AddMinutes(30), now).Should().Be(now);
        TelemetryRules.SampleTime(now.AddDays(-8), now).Should().Be(now);
    }

    [Fact]
    public void Text_is_trimmed_and_cut_to_the_column_size()
    {
        TelemetryRules.Trim("  hola  ", 50).Should().Be("hola");
        TelemetryRules.Trim("abcdef", 3).Should().Be("abc");
        TelemetryRules.Trim("   ", 3).Should().BeNull();
        TelemetryRules.Trim(null, 3).Should().BeNull();
    }

    [Theory]
    [InlineData("2024-09-10", true)]
    [InlineData("10/09/2024", false)]
    [InlineData("1980-01-01", false)]
    [InlineData("2999-01-01", false)]
    [InlineData(null, false)]
    public void Install_dates_must_be_iso_and_plausible(string? value, bool valid) =>
        TelemetryRules.TryParseDate(value, out _).Should().Be(valid);
}

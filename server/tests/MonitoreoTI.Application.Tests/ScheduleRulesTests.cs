using FluentAssertions;
using MonitoreoTI.Application.Remote;

namespace MonitoreoTI.Application.Tests;

public sealed class ScheduleRulesTests
{
    // Viernes 30 de octubre de 2026, 21:00 en República Dominicana (UTC-4) = sábado 01:00 UTC.
    private static readonly DateTime FridayNightUtc = new(2026, 10, 31, 1, 0, 0, DateTimeKind.Utc);

    private static DateTime Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void Daily_runs_today_if_the_hour_has_not_passed_in_dominican_time_and_tomorrow_otherwise()
    {
        var at23 = new ScheduleSpec("diaria", new TimeOnly(23, 0), null, null, null);
        var at02 = new ScheduleSpec("diaria", new TimeOnly(2, 0), null, null, null);

        ScheduleRules.Next(at23, FridayNightUtc).Should().Be(Utc(10, 31, 3)); // viernes 23:00 RD
        ScheduleRules.Next(at02, FridayNightUtc).Should().Be(Utc(10, 31, 6)); // sábado 02:00 RD
    }

    [Fact]
    public void Weekly_picks_the_next_marked_day()
    {
        var mondayAndFriday = new ScheduleSpec("semanal", new TimeOnly(18, 30), null, ScheduleRules.Monday | ScheduleRules.Friday, null);

        // El viernes a las 18:30 ya pasó: el siguiente es el lunes 2 de noviembre a las 18:30 RD.
        ScheduleRules.Next(mondayAndFriday, FridayNightUtc).Should().Be(Utc(11, 2, 22, 30));
        ScheduleRules.Next(new ScheduleSpec("semanal", new TimeOnly(1, 0), null, 0, null), FridayNightUtc).Should().BeNull();
    }

    [Fact]
    public void Monthly_uses_the_last_day_when_the_month_is_shorter()
    {
        var day31 = new ScheduleSpec("mensual", new TimeOnly(3, 0), null, null, 31);

        ScheduleRules.Next(day31, FridayNightUtc).Should().Be(Utc(10, 31, 7)); // sábado 31 a las 03:00 RD
        ScheduleRules.Next(day31, Utc(10, 31, 8)).Should().Be(Utc(11, 30, 7)); // noviembre tiene 30 días
    }

    [Fact]
    public void Once_runs_a_single_time()
    {
        var once = new ScheduleSpec("una_vez", new TimeOnly(9, 0), new DateOnly(2026, 11, 2), null, null);

        ScheduleRules.Next(once, FridayNightUtc).Should().Be(Utc(11, 2, 13));
        ScheduleRules.Next(once, Utc(11, 2, 13)).Should().BeNull();
    }

    [Theory]
    [InlineData("diaria", null, null, "Todos los días a las 02:00")]
    [InlineData("semanal", 31, null, "De lunes a viernes a las 02:00")]
    [InlineData("semanal", 17, null, "Lunes y viernes a las 02:00")]
    [InlineData("mensual", null, 15, "El día 15 de cada mes a las 02:00")]
    public void It_is_described_in_plain_words(string frequency, int? days, int? monthDay, string expected) =>
        ScheduleRules.Describe(new ScheduleSpec(frequency, new TimeOnly(2, 0), null, days, monthDay)).Should().Be(expected);

    [Theory]
    [InlineData("0.1.0", "0.1.1", true)]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("1.2.0", "1.2.0", false)]
    [InlineData("1.3.0", "1.2.9", false)]
    [InlineData("", "0.1.0", true)]
    public void Versions_compare_by_numbers(string installed, string available, bool older) =>
        VersionRules.IsOlder(installed, available).Should().Be(older);
}

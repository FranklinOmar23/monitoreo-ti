using FluentAssertions;
using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Tests;

public class HealthRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);

    private static MachineSnapshot Machine(
        DateTime? lastReport = null,
        bool disconnected = false,
        IReadOnlyList<VolumeSnapshot>? volumes = null,
        IReadOnlyList<MetricSample>? metrics = null,
        IReadOnlyList<EventSnapshot>? events = null,
        IReadOnlyList<StoppedService>? services = null,
        IReadOnlyList<ComponentSnapshot>? components = null) =>
        new(1, "CONTA-03", disconnected, lastReport ?? Now.AddMinutes(-1),
            volumes ?? [], metrics ?? [], events ?? [], services ?? [], components ?? []);

    /// <summary>Una muestra por minuto durante los últimos <paramref name="minutes"/> minutos.</summary>
    private static List<MetricSample> Samples(int minutes, decimal? cpu = null, decimal? ram = null, decimal? temp = null) =>
        Enumerable.Range(0, minutes).Select(i => new MetricSample(Now.AddMinutes(-i), cpu, ram, temp)).ToList();

    private static IReadOnlyList<Finding> Evaluate(MachineSnapshot machine, RuleSet? rules = null) =>
        HealthRules.Evaluate(machine, rules ?? new RuleSet(), Now);

    [Fact]
    public void Healthy_machine_has_no_findings() =>
        Evaluate(Machine(volumes: [new("C:", 476, 300)], metrics: Samples(30, 20, 40, 50))).Should().BeEmpty();

    [Theory]
    [InlineData(476, 80, false)]   // 83 %
    [InlineData(476, 39, false)]   // 92 %: no llega a 95
    [InlineData(476, 10, true)]    // 98 %
    public void Full_disk_is_reported_per_volume(double total, double free, bool critical)
    {
        var findings = Evaluate(Machine(volumes: [new("C:", (decimal)total, (decimal)free)]));
        var finding = findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("disco_lleno:C:");
        finding.Level.Should().Be(critical ? FindingLevel.Critical : FindingLevel.Warning);
        finding.Advice.Should().NotBeNullOrWhiteSpace();
        finding.Source.Should().StartWith("Unidad C:");
    }

    [Fact]
    public void Cpu_needs_the_rule_duration()
    {
        // 10 minutos al 99 % no alcanzan los 15 de la regla.
        Evaluate(Machine(metrics: Samples(10, cpu: 99))).Should().BeEmpty();

        var warning = Evaluate(Machine(metrics: Samples(15, cpu: 90))).Should().ContainSingle().Subject;
        warning.Code.Should().Be("cpu_alta");
        warning.Level.Should().Be(FindingLevel.Warning);

        Evaluate(Machine(metrics: Samples(30, cpu: 99))).Single().Level.Should().Be(FindingLevel.Critical);
    }

    [Fact]
    public void Ram_uses_the_average_of_five_minutes()
    {
        Evaluate(Machine(metrics: Samples(3, ram: 99))).Should().BeEmpty();
        Evaluate(Machine(metrics: Samples(5, ram: 97))).Single().Should().Match<Finding>(f => f.Code == "ram_alta" && f.Level == FindingLevel.Critical);
    }

    [Fact]
    public void High_temperature_uses_the_latest_reading() =>
        Evaluate(Machine(metrics: Samples(1, temp: 85))).Single().Should()
            .Match<Finding>(f => f.Code == "temperatura_alta" && f.Level == FindingLevel.Warning);

    [Fact]
    public void Silent_machine_is_reported_and_stale_data_is_ignored()
    {
        var findings = Evaluate(Machine(lastReport: Now.AddMinutes(-45), volumes: [new("C:", 100, 1)], metrics: Samples(30, cpu: 99)));
        var finding = findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("sin_reportar");
        finding.Level.Should().Be(FindingLevel.Critical);
    }

    [Fact]
    public void Stopped_services_are_critical_with_their_own_code()
    {
        var finding = Evaluate(Machine(services: [new("Spooler", "Cola de impresión")])).Should().ContainSingle().Subject;
        finding.Code.Should().Be("servicio:Spooler");
        finding.Level.Should().Be(FindingLevel.Critical);
        finding.Message.Should().Contain("Cola de impresión");
    }

    [Fact]
    public void Critical_events_use_the_catalog_explanation()
    {
        var events = new[]
        {
            new EventSnapshot(Now.AddHours(-1), "Microsoft-Windows-Kernel-Power", 41),
            new EventSnapshot(Now.AddHours(-3), "Microsoft-Windows-Kernel-Power", 41),
        };
        var finding = Evaluate(Machine(events: events)).Should().ContainSingle().Subject;
        finding.Code.Should().Be("errores_criticos");
        finding.Level.Should().Be(FindingLevel.Warning);
        finding.Detail.Should().Contain("2 veces");
        finding.Advice.Should().Contain("UPS");
        finding.Source.Should().StartWith("Kernel-Power 41");

        var three = events.Append(new EventSnapshot(Now.AddHours(-5), "disk", 7)).ToList();
        Evaluate(Machine(events: three)).Single().Level.Should().Be(FindingLevel.Critical);
    }

    [Fact]
    public void Damaged_disk_asks_for_backup()
    {
        var finding = Evaluate(Machine(components: [new("disco", "WD Blue 1TB", "critico", "148 sectores dañados · 41 °C")]))
            .Should().ContainSingle().Subject;
        finding.Code.Should().Be("disco_salud:WD Blue 1TB");
        finding.Level.Should().Be(FindingLevel.Critical);
        finding.Message.Should().Be("Disco con sectores dañados");
        finding.Advice.Should().Contain("Respalda");
    }

    [Fact]
    public void Antivirus_alert_only_when_none_is_active()
    {
        var passive = new ComponentSnapshot("antivirus", "Windows Defender", "critico", null);
        var active = new ComponentSnapshot("antivirus", "ESET", "sano", null);
        Evaluate(Machine(components: [passive, active])).Should().BeEmpty();
        Evaluate(Machine(components: [passive])).Single().Code.Should().Be("antivirus");
    }

    [Fact]
    public void Company_rules_override_and_disable_defaults()
    {
        var rules = new RuleSet(new Dictionary<string, RuleThreshold>
        {
            ["disco"] = new(50, 60),
            ["servicio_detenido"] = new(null, 1, Active: false),
        });
        var findings = Evaluate(Machine(volumes: [new("D:", 100, 45)], services: [new("Spooler", null)]), rules);
        findings.Should().ContainSingle().Which.Should().Match<Finding>(f => f.Code == "disco_lleno:D:" && f.Level == FindingLevel.Warning);
    }
}

public class EventCatalogTests
{
    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Power", 41)]
    [InlineData("EventLog", 6008)]
    [InlineData("disk", 7)]
    [InlineData("Service Control Manager", 7031)]
    public void Known_events_have_a_plain_explanation(string source, int code)
    {
        var explanation = EventCatalog.Explain(source, code);
        explanation.Should().NotBeNull();
        explanation!.Title.Should().NotBeNullOrWhiteSpace();
        explanation.Advice.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Lookup_ignores_case() =>
        EventCatalog.Explain("MICROSOFT-WINDOWS-KERNEL-POWER", 41).Should().NotBeNull();

    [Fact]
    public void Unknown_events_return_null() => EventCatalog.Explain("Algo", 12345).Should().BeNull();
}

using FluentAssertions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class SnmpRulesTests
{
    private static SnmpInterfazData Iface(int index, int? type, string name, string admin = "up", string oper = "up") =>
        new(index, type, name, null, admin, oper, 1000, 0, 0, 0, 0, 0);

    [Fact]
    public void Ports_are_the_physical_interfaces_in_ifIndex_order()
    {
        var interfaces = new[]
        {
            Iface(10102, 117, "Gi1/0/2"),
            Iface(1, 53, "Vlan1"),
            Iface(10101, 117, "Gi1/0/1"),
            Iface(5001, 161, "Po1"),
            Iface(10149, 6, "Te1/1/1"),
            Iface(20, null, "Loopback0"),
        };

        var map = SnmpRules.MapToPortNumbers(interfaces, [1, 2, 3, 4]);

        map.Should().HaveCount(3);
        map[1].Nombre.Should().Be("Gi1/0/1");
        map[2].Nombre.Should().Be("Gi1/0/2");
        map[3].Nombre.Should().Be("Te1/1/1");
    }

    [Theory]
    [InlineData("down", "down", null, false, "deshabilitado")]
    [InlineData("up", "up", 5, false, "conectado")]
    [InlineData("up", "up", 250, false, "errores")]
    [InlineData("up", "down", null, false, "libre")]
    [InlineData("up", "down", null, true, "caido")]
    public void Port_status(string admin, string oper, int? errorsPerHour, bool knownMachine, string expected) =>
        SnmpRules.PortStatus(Iface(1, 6, "Gi1/0/1", admin, oper), errorsPerHour, 100, knownMachine).Should().Be(expected);

    [Fact]
    public void Error_rate_uses_the_counter_delta_and_ignores_resets()
    {
        var t0 = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);
        SnmpRules.ErrorsPerHour(100, t0, 150, t0.AddMinutes(5)).Should().Be(600);
        SnmpRules.ErrorsPerHour(100, t0, 20, t0.AddMinutes(5)).Should().BeNull("el contador se reinició");
        SnmpRules.ErrorsPerHour(null, null, 20, t0).Should().BeNull();
    }

    [Theory]
    [InlineData(true, 1, false, true)]
    [InlineData(false, 11, false, true)]
    [InlineData(false, 11, true, false)]
    [InlineData(false, 2, false, false)]
    public void Trunks(bool neighbor, int macs, bool vmHost, bool expected) =>
        SnmpRules.IsTrunk(neighbor, macs, vmHost).Should().Be(expected);

    [Theory]
    [InlineData("DA:A1:19:00:00:01", true)]
    [InlineData("3C:52:82:A1:00:03", false)]
    public void Random_macs(string mac, bool random) => SnmpRules.IsRandomMac(mac).Should().Be(random);

    [Fact]
    public void Firmware_comes_from_sysDescr() =>
        SnmpRules.FirmwareFrom("Cisco IOS Software [Cupertino], Catalyst L3 Switch Software (CAT9K_LITE_IOSXE), Version 17.9.4, RELEASE")
            .Should().Be("17.9.4");
}

public class NetworkHealthTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static DeviceSnapshot Device(bool responding, DateTime since, params PortSnapshot[] ports) =>
        new(1, "SW-ACC-02", true, since, responding, responding ? null : "El dispositivo no respondió por SNMP.", ports);

    [Fact]
    public void A_silent_switch_warns_at_10_and_is_critical_at_30_minutes()
    {
        NetworkHealth.Evaluate(Device(false, Now.AddMinutes(-5)), new RuleSet(), Now).Should().BeEmpty();
        NetworkHealth.Evaluate(Device(false, Now.AddMinutes(-12)), new RuleSet(), Now).Single().Level.Should().Be(FindingLevel.Warning);
        var critical = NetworkHealth.Evaluate(Device(false, Now.AddMinutes(-31)), new RuleSet(), Now).Single();
        critical.Level.Should().Be(FindingLevel.Critical);
        critical.Detail.Should().Contain("no respondió");
    }

    [Fact]
    public void Port_errors_and_a_known_machine_down()
    {
        var findings = NetworkHealth.Evaluate(Device(true, Now,
            new PortSnapshot(10, "SW-ACC-02 Gi1/0/5", "errores", 1500, null),
            new PortSnapshot(11, "SW-ACC-02 Gi1/0/12", "caido", null, "CONTA-03"),
            new PortSnapshot(12, "SW-ACC-02 Gi1/0/13", "conectado", 50, null)), new RuleSet(), Now);

        findings.Should().HaveCount(2);
        findings.Single(f => f.Code == "puerto_errores:10").Level.Should().Be(FindingLevel.Critical);
        findings.Single(f => f.Code == "puerto_caido:11").Message.Should().Contain("CONTA-03");
    }

    [Fact]
    public void Ports_are_not_judged_while_the_switch_does_not_answer() =>
        NetworkHealth.Evaluate(Device(false, Now.AddMinutes(-1), new PortSnapshot(11, "x", "caido", null, "CONTA-03")), new RuleSet(), Now)
            .Should().BeEmpty();

    [Fact]
    public void A_device_without_collector_is_not_evaluated() =>
        NetworkHealth.Evaluate(Device(false, Now.AddDays(-3)) with { Polled = false }, new RuleSet(), Now).Should().BeEmpty();
}

public class SnmpCredentialSealTests
{
    [Fact]
    public void Only_the_agent_with_that_credential_opens_it()
    {
        var (token, hash) = AgentTokens.Generate(1);
        var (other, _) = AgentTokens.Generate(1);
        var sealedValue = SnmpCredentialSeal.Seal(hash, """{"auth":"clave-auth-1","priv":"clave-priv-2"}""");

        sealedValue.Should().NotContain("clave");
        SnmpCredentialSeal.Open(token, sealedValue).Should().Contain("clave-auth-1");
        var wrong = () => SnmpCredentialSeal.Open(other, sealedValue);
        wrong.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}

public class DocumentedPeerTests
{
    private static readonly Dictionary<int, PathPort> Ports = new()
    {
        [1] = new(1, "Gi1/0/48", 10, "SW-ACC-01", "switch", true, true),
        [2] = new(2, "P48", 20, "PP-01", "patch_panel", false, false),
        [3] = new(3, "Te1/1/1", 30, "SW-CORE-01", "switch", true, true),
        [4] = new(4, "Gi1/0/1", 10, "SW-ACC-01", "switch", true, false),
    };

    [Fact]
    public void Follows_patch_panels_to_the_active_port()
    {
        var cables = new List<PathLink> { new(1, 2, null, null, false), new(2, 3, null, null, false), new(4, null, 99, null, false) };
        CablePath.DocumentedPeer(1, Ports, cables).Should().Be(3);
        CablePath.DocumentedPeer(3, Ports, cables).Should().Be(1);
        CablePath.DocumentedPeer(4, Ports, cables).Should().BeNull("termina en una roseta");
    }

    [Fact]
    public void Lldp_links_are_not_documentation() =>
        CablePath.DocumentedPeer(1, Ports, [new PathLink(1, 3, null, null, true)]).Should().BeNull();
}

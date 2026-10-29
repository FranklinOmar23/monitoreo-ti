using FluentAssertions;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class RackLayoutTests
{
    private static readonly RackSlot[] Placed =
    [
        new(1, "SW-CORE-01", 40, 1),
        new(2, "PP-A01-01", 35, 2),
    ];

    [Theory]
    [InlineData(1, 1)]
    [InlineData(41, 2)]
    [InlineData(37, 3)]
    public void Free_units_are_accepted(int position, int height) =>
        RackLayout.Check(42, Placed, position, height).Should().BeNull();

    [Fact]
    public void Overlap_names_the_other_device() =>
        RackLayout.Check(42, Placed, 36, 1).Should().Contain("PP-A01-01").And.Contain("U 35–36");

    [Fact]
    public void A_device_cannot_exceed_the_rack() =>
        RackLayout.Check(42, Placed, 42, 2).Should().Contain("hasta la U 43");

    [Fact]
    public void Moving_a_device_ignores_its_own_slot() =>
        RackLayout.Check(42, Placed, 41, 1, movingDeviceId: 1).Should().BeNull();

    [Fact]
    public void Zero_height_accessories_take_no_space() =>
        RackLayout.Check(42, Placed, 40, 0).Should().BeNull();
}

public class PortPlanTests
{
    [Fact]
    public void Ports_follow_the_model_description()
    {
        var ports = PortPlan.FromModel(52, "48 RJ45 + 4 SFP+", isPatchPanel: false);
        ports.Should().HaveCount(52);
        ports[0].Should().Be(new PlannedPort(1, "1", "rj45"));
        ports[47].Type.Should().Be("rj45");
        ports[48].Should().Be(new PlannedPort(49, "SFP+1", "sfp_plus"));
        ports[51].Name.Should().Be("SFP+4");
    }

    [Fact]
    public void Patch_panel_ports_are_numbered_with_P() =>
        PortPlan.FromModel(24, "24 RJ45 Cat6", isPatchPanel: true).Select(p => p.Name).Should().StartWith(["P1", "P2"]);

    [Fact]
    public void Missing_description_means_rj45() =>
        PortPlan.FromModel(8, null, isPatchPanel: false).Should().OnlyContain(p => p.Type == "rj45");
}

public class CablePathTests
{
    // CONTA-03 -> P-2-15 -> PP-A01-01 P12 -> SW-ACC-02 Gi1/0/12 ... SW-ACC-02 Te1/1/1 (troncal) -> SW-CORE-01 Te1/1/2
    private static readonly Dictionary<int, PathPort> Ports = new()
    {
        [10] = new(10, "P12", 3, "PP-A01-01", "patch_panel", false, false),
        [20] = new(20, "Gi1/0/12", 2, "SW-ACC-02", "switch", true, false),
        [21] = new(21, "Te1/1/1", 2, "SW-ACC-02", "switch", true, true),
        [30] = new(30, "Te1/1/2", 1, "SW-CORE-01", "switch", true, true),
    };

    private static readonly PathLink[] Links =
    [
        new(10, null, 5, "C-0215", false),
        new(10, 20, null, "PC-12", false),
        new(21, 30, null, null, true),
    ];

    [Fact]
    public void From_an_outlet_to_the_core()
    {
        var hops = CablePath.FromOutlet(5, "P-2-15", Ports, Links);
        hops.Select(h => h.Name).Should().Equal(
            "P-2-15", "PP-A01-01 P12", "SW-ACC-02 Gi1/0/12", "SW-ACC-02 Te1/1/1", "SW-CORE-01 Te1/1/2");
        hops[1].CableLabel.Should().Be("C-0215");
        hops[4].CableLabel.Should().Be("LLDP");
    }

    [Fact]
    public void From_a_patch_panel_port_includes_its_outlet() =>
        CablePath.FromPort(10, Ports, Links, new Dictionary<int, string> { [5] = "P-2-15" })
            .First().Name.Should().Be("P-2-15");

    [Fact]
    public void An_outlet_without_cable_is_just_the_outlet() =>
        CablePath.FromOutlet(9, "P-9-9", Ports, Links).Should().ContainSingle();

    [Fact]
    public void The_path_stops_at_the_core_instead_of_going_down_another_uplink()
    {
        // El core tiene un segundo uplink hacia SW-ACC-03: la ruta no debe bajar por él.
        var ports = new Dictionary<int, PathPort>(Ports)
        {
            [31] = new(31, "Te1/1/3", 1, "SW-CORE-01", "switch", true, true),
            [40] = new(40, "Te1/1/1", 4, "SW-ACC-03", "switch", true, true),
        };
        var links = Links.Append(new PathLink(40, 31, null, null, true)).ToArray();
        CablePath.FromOutlet(5, "P-2-15", ports, links).Last().Name.Should().Be("SW-CORE-01 Te1/1/2");
    }

    [Fact]
    public void Loops_between_switches_do_not_hang()
    {
        var ports = new Dictionary<int, PathPort>(Ports) { [31] = new(31, "Te1/1/3", 1, "SW-CORE-01", "switch", true, true) };
        var links = Links.Append(new PathLink(31, 21, null, null, true)).ToArray();
        CablePath.FromPort(20, ports, links, new Dictionary<int, string>()).Should().HaveCountLessThan(10);
    }
}

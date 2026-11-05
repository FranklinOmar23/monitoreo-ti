using FluentAssertions;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class IpRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 15, 0, 0, DateTimeKind.Utc);

    private static uint Ip(string text) => IpRules.TryParse(text, out var ip) ? ip : throw new ArgumentException(text);

    [Theory]
    [InlineData("192.168.3.10", true)]
    [InlineData("10.0.0.0", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.3", false)]
    [InlineData("192.168.3.256", false)]
    [InlineData("192.168.03.1000", false)]
    [InlineData("192.168.3.-1", false)]
    [InlineData("a.b.c.d", false)]
    [InlineData("", false)]
    public void Parses_only_dotted_ipv4(string text, bool valid)
    {
        IpRules.TryParse(text, out _).Should().Be(valid);
    }

    [Fact]
    public void Round_trips_to_text()
    {
        IpRules.ToText(Ip("172.16.40.23")).Should().Be("172.16.40.23");
    }

    [Fact]
    public void Cidr_is_taken_to_its_network_address()
    {
        IpRules.TryParseCidr("192.168.3.77/24", out var network, out var prefix).Should().BeTrue();
        IpRules.ToText(network).Should().Be("192.168.3.0");
        prefix.Should().Be(24);
    }

    [Theory]
    [InlineData("192.168.0.0/16")]
    [InlineData("192.168.3.0/31")]
    [InlineData("192.168.3.0")]
    [InlineData("192.168.3.0/x")]
    public void Rejects_networks_too_big_or_too_small_to_sweep(string text)
    {
        IpRules.TryParseCidr(text, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Hosts_skip_the_network_and_broadcast_addresses()
    {
        var hosts = IpRules.Hosts(Ip("192.168.3.0"), 24).ToList();

        hosts.Should().HaveCount(254);
        IpRules.ToText(hosts[0]).Should().Be("192.168.3.1");
        IpRules.ToText(hosts[^1]).Should().Be("192.168.3.254");
        IpRules.HostCount(22).Should().Be(1022);
    }

    [Fact]
    public void Contains_respects_the_prefix()
    {
        IpRules.Contains(Ip("10.1.4.0"), 22, Ip("10.1.7.200")).Should().BeTrue();
        IpRules.Contains(Ip("10.1.4.0"), 22, Ip("10.1.8.1")).Should().BeFalse();
    }

    [Fact]
    public void Reserved_wins_even_if_the_address_is_in_use()
    {
        IpRules.Classify(Now.AddMinutes(-5), reserved: true, knownMachine: true, 30, Now).Should().Be(IpState.Reserved);
    }

    [Fact]
    public void Seen_in_the_last_day_is_in_use_known_or_not()
    {
        IpRules.Classify(Now.AddHours(-2), false, knownMachine: true, 30, Now).Should().Be(IpState.InUse);
        IpRules.Classify(Now.AddHours(-2), false, knownMachine: false, 30, Now).Should().Be(IpState.Unidentified);
    }

    [Fact]
    public void Seen_days_ago_is_recent_until_the_free_window_passes()
    {
        IpRules.Classify(Now.AddDays(-3), false, true, 30, Now).Should().Be(IpState.Recent);
        IpRules.Classify(Now.AddDays(-31), false, true, 30, Now).Should().Be(IpState.Free);
        IpRules.Classify(null, false, false, 30, Now).Should().Be(IpState.Free);
    }

    [Fact]
    public void Suggests_the_lowest_free_address()
    {
        var addresses = new[]
        {
            (Ip("192.168.3.3"), IpState.Free),
            (Ip("192.168.3.1"), IpState.Reserved),
            (Ip("192.168.3.2"), IpState.Recent),
            (Ip("192.168.3.4"), IpState.Free),
        };

        IpRules.SuggestFree(addresses).Should().Be(Ip("192.168.3.3"));
        IpRules.SuggestFree([(Ip("192.168.3.1"), IpState.InUse)]).Should().BeNull();
    }

    [Fact]
    public void Reservation_ranges_must_be_inside_the_subnet()
    {
        var network = Ip("192.168.3.0");
        IpRules.IsValidRange(network, 24, Ip("192.168.3.100"), Ip("192.168.3.199")).Should().BeTrue();
        IpRules.IsValidRange(network, 24, Ip("192.168.3.200"), Ip("192.168.3.100")).Should().BeFalse();
        IpRules.IsValidRange(network, 24, Ip("192.168.3.0"), Ip("192.168.3.10")).Should().BeFalse();
        IpRules.IsValidRange(network, 24, Ip("192.168.3.250"), Ip("192.168.3.255")).Should().BeFalse();
        IpRules.IsValidRange(network, 24, Ip("192.168.3.10"), Ip("192.168.4.10")).Should().BeFalse();
    }

    [Theory]
    [InlineData("80:5E:C0:4A:12:9F", true)]
    [InlineData("FF:FF:FF:FF:FF:FF", false)]
    [InlineData("01:00:5E:00:00:FB", false)]
    [InlineData("00:00:00:00:00:00", false)]
    public void Broadcast_and_multicast_macs_are_not_devices(string mac, bool unicast)
    {
        IpRules.IsUnicastMac(mac).Should().Be(unicast);
    }
}

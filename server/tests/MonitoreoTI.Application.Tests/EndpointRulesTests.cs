using FluentAssertions;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Application.Tests;

public class EndpointRulesTests
{
    [Theory]
    [InlineData("computadora", null, "pc", false)]
    [InlineData("laptop", "Dell Inc.", "pc", false)]
    [InlineData("servidor", null, "servidor", false)]
    [InlineData("impresora", "Hewlett Packard", "impresora", false)]
    [InlineData(null, "Xiamen Yealink Network Technology", "telefono", true)]
    [InlineData(null, "Hangzhou Hikvision Digital Technology", "camara", true)]
    [InlineData(null, "Seiko Epson Corporation", "impresora", true)]
    [InlineData(null, "Ubiquiti Inc", "access_point", true)]
    [InlineData(null, "Espressif Inc.", "desconocido", false)]
    [InlineData(null, null, "desconocido", false)]
    public void The_known_type_wins_and_otherwise_the_vendor_suggests_one(string? machineType, string? vendor, string category, bool inferred)
    {
        EndpointRules.Category(machineType, vendor).Should().Be((category, inferred));
    }
}

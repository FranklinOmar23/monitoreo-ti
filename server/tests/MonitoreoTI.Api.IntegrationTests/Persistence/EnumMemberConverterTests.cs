using FluentAssertions;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence.Conversions;

namespace MonitoreoTI.Api.IntegrationTests.Persistence;

public class EnumMemberConverterTests
{
    [Theory]
    [InlineData(MachineStatus.Healthy, "sano")]
    [InlineData(MachineStatus.Disconnected, "desconectado")]
    [InlineData(MachineStatus.Retired, "baja")]
    public void Converts_machine_status_to_database_text_and_back(MachineStatus status, string text)
    {
        EnumMemberConverter<MachineStatus>.ToProvider(status).Should().Be(text);
        EnumMemberConverter<MachineStatus>.FromProvider(text).Should().Be(status);
    }

    [Fact]
    public void Converts_comparison_operators()
    {
        EnumMemberConverter<ComparisonOperator>.ToProvider(ComparisonOperator.GreaterThanOrEqual).Should().Be(">=");
        EnumMemberConverter<ComparisonOperator>.FromProvider("<").Should().Be(ComparisonOperator.LessThan);
    }

    [Fact]
    public void Unknown_database_value_fails_loudly()
    {
        var read = () => EnumMemberConverter<MachineStatus>.FromProvider("verde");

        read.Should().Throw<InvalidOperationException>().WithMessage("*verde*");
    }
}

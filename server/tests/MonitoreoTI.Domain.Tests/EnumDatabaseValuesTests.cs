using System.Reflection;
using System.Runtime.Serialization;
using FluentAssertions;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Tests;

public class EnumDatabaseValuesTests
{
    public static TheoryData<Type> DomainEnums()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(MachineStatus).Assembly.GetTypes().Where(t => t.IsEnum))
            data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(DomainEnums))]
    public void Every_member_declares_its_database_value(Type enumType)
    {
        var fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);

        fields.Should().OnlyContain(
            f => !string.IsNullOrWhiteSpace(f.GetCustomAttribute<EnumMemberAttribute>()!.Value),
            "cada valor se guarda con el texto de su [EnumMember] (CHECK de la columna)");

        fields.Select(f => f.GetCustomAttribute<EnumMemberAttribute>()!.Value)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Machine_status_matches_the_traffic_light_values()
    {
        typeof(MachineStatus).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<EnumMemberAttribute>()!.Value)
            .Should().BeEquivalentTo("sano", "advertencia", "critico", "desconectado", "baja");
    }
}

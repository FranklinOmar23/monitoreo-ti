using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MonitoreoTI.Infrastructure.Persistence.Conversions;

/// <summary>
/// Guarda un enum con el texto de su [EnumMember] (ej. MachineStatus.Healthy -> 'sano'),
/// que es el valor que exige el CHECK de la columna.
/// </summary>
public sealed class EnumMemberConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> ToDatabase = BuildMap();
    private static readonly Dictionary<string, TEnum> FromDatabase =
        ToDatabase.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    public EnumMemberConverter()
        : base(value => ToProvider(value), value => FromProvider(value))
    {
    }

    public static string ToProvider(TEnum value) =>
        ToDatabase.TryGetValue(value, out var text)
            ? text
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"Valor no definido en {typeof(TEnum).Name}.");

    public static TEnum FromProvider(string value) =>
        FromDatabase.TryGetValue(value, out var member)
            ? member
            : throw new InvalidOperationException($"La base devolvió '{value}', que no corresponde a ningún valor de {typeof(TEnum).Name}.");

    private static Dictionary<TEnum, string> BuildMap()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = field.GetCustomAttribute<EnumMemberAttribute>()
                ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{field.Name} no tiene [EnumMember].");
            map[(TEnum)field.GetValue(null)!] = attribute.Value
                ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{field.Name} tiene [EnumMember] sin Value.");
        }
        return map;
    }
}

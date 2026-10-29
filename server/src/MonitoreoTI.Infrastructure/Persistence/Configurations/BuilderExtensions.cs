using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MonitoreoTI.Infrastructure.Persistence.Configurations;

internal static class BuilderExtensions
{
    public const string UtcNow = "SYSUTCDATETIME()";
    public const string NewGuid = "NEWID()";

    /// <summary>Columna VARCHAR(n): evita conversiones implícitas NVARCHAR/VARCHAR que impiden usar índices.</summary>
    public static PropertyBuilder<T> Varchar<T>(this PropertyBuilder<T> builder, int maxLength) =>
        builder.HasMaxLength(maxLength).IsUnicode(false);

    /// <summary>Columna con DEFAULT SYSUTCDATETIME(): si el valor es default(DateTime), lo llena la base.</summary>
    public static PropertyBuilder<DateTime> DefaultUtcNow(this PropertyBuilder<DateTime> builder) =>
        builder.HasDefaultValueSql(UtcNow);

    /// <summary>Columna con DEFAULT NEWID(): si el valor es Guid.Empty, lo genera la base.</summary>
    public static PropertyBuilder<Guid> DefaultNewGuid(this PropertyBuilder<Guid> builder) =>
        builder.HasDefaultValueSql(NewGuid);
}

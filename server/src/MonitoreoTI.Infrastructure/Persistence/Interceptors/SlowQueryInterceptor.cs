using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MonitoreoTI.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Registra solo las consultas lentas (decisión 83), con su tiempo y el SQL sin los valores de los parámetros. Reemplaza
/// el registro de cada consulta, que en desarrollo también frenaba la API.
/// </summary>
public sealed class SlowQueryInterceptor(ILogger<SlowQueryInterceptor> logger) : DbCommandInterceptor
{
    /// <summary>A partir de cuánto una consulta se considera lenta.</summary>
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(200);

    private void Check(DbCommand command, CommandExecutedEventData data)
    {
        if (data.Duration < Threshold)
            return;
        var sql = command.CommandText.Length > 2000 ? command.CommandText[..2000] + "…" : command.CommandText;
        logger.LogWarning("Consulta lenta ({Ms} ms): {Sql}", (int)data.Duration.TotalMilliseconds, sql);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(command, eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }
}

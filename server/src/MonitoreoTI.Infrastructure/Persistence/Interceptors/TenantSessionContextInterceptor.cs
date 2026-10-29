using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MonitoreoTI.Application.Abstractions;

namespace MonitoreoTI.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Al abrir cada conexión fija SESSION_CONTEXT, que usa la política de Row-Level Security
/// (seguridad.politica_empresa). Los valores quedan de solo lectura durante la conexión;
/// el pool de conexiones los limpia al reutilizarla (sp_reset_connection).
/// Sin empresa ni superadministrador no se fija nada: las tablas de negocio se ven vacías.
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command?.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        if (command is not null)
            await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand? CreateCommand(DbConnection connection)
    {
        if (tenant.IsSuperAdmin)
            return Build(connection, "es_superadmin", true);

        if (tenant.CompanyId is int companyId)
            return Build(connection, "empresa_id", companyId);

        return null;
    }

    private static DbCommand Build(DbConnection connection, string key, object value)
    {
        var command = connection.CreateCommand();
        command.CommandText = "EXEC sys.sp_set_session_context @key = @key, @value = @value, @read_only = 1;";

        var keyParameter = command.CreateParameter();
        keyParameter.ParameterName = "@key";
        keyParameter.Value = key;
        command.Parameters.Add(keyParameter);

        var valueParameter = command.CreateParameter();
        valueParameter.ParameterName = "@value";
        valueParameter.Value = value;
        command.Parameters.Add(valueParameter);

        return command;
    }
}

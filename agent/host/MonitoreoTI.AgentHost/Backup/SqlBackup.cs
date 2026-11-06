using System.Runtime.Versioning;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace MonitoreoTI.AgentHost.Backup;

public sealed record SqlDatabase(string Name, long SizeMb, string? RecoveryModel);
public sealed record SqlInstance(string Name, string? Error, List<SqlDatabase> Databases);

/// <summary>
/// Respaldos de SQL Server (decisión 82) con el propio BACKUP DATABASE, seguro con la base en uso. El agente entra como
/// SYSTEM con autenticación de Windows: el login NT AUTHORITY\SYSTEM necesita el rol db_backupoperator en cada base.
/// SQL Server escribe el .bak en su carpeta de respaldos (su cuenta de servicio sí puede) y desde ahí se copia al destino.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SqlBackup
{
    /// <summary>Las instancias instaladas en este servidor (MSSQLSERVER es la predeterminada).</summary>
    public static List<string> Instances()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
        return key?.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    public static string ConnectionString(string instance) => new SqlConnectionStringBuilder
    {
        DataSource = instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "." : $@".\{instance}",
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        ConnectTimeout = 15,
        ApplicationName = "Centinela respaldos",
        InitialCatalog = "master",
    }.ConnectionString;

    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static async Task<SqlInstance> DescribeAsync(string instance, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString(instance));
            await connection.OpenAsync(ct);
            await using var command = new SqlCommand(
                """
                SELECT d.name, d.recovery_model_desc, CAST(SUM(CAST(f.size AS BIGINT)) * 8 / 1024 AS BIGINT)
                FROM sys.databases d JOIN sys.master_files f ON f.database_id = d.database_id
                WHERE d.database_id <> 2 AND d.state_desc = 'ONLINE'
                GROUP BY d.name, d.recovery_model_desc ORDER BY d.name
                """, connection);
            var list = new List<SqlDatabase>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                list.Add(new SqlDatabase(reader.GetString(0), reader.GetInt64(2), reader.IsDBNull(1) ? null : reader.GetString(1)));
            return new SqlInstance(instance, null, list);
        }
        catch (SqlException ex)
        {
            return new SqlInstance(instance, Explain(ex), []);
        }
    }

    /// <summary>Errores de SQL Server en palabras del técnico.</summary>
    public static string Explain(SqlException ex) => ex.Number switch
    {
        18456 or 4060 => "SQL Server no deja entrar al agente: dé al login NT AUTHORITY\\SYSTEM el rol db_backupoperator en las bases que se respaldan.",
        262 or 3013 when ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase)
            => "El agente no tiene permiso para respaldar: dé al login NT AUTHORITY\\SYSTEM el rol db_backupoperator en esa base.",
        2 or 53 or -1 => "No se pudo conectar a SQL Server (¿el servicio está detenido?).",
        3201 => "SQL Server no pudo escribir el archivo del respaldo en su carpeta de respaldos (permisos o espacio).",
        _ => $"SQL Server: {ex.Message}",
    };

    public sealed record Prepared(string Instance, string StagingFolder, bool Compression);

    /// <summary>Carpeta de respaldos de la instancia y si admite compresión (Express no).</summary>
    public static async Task<Prepared> PrepareAsync(string instance, CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString(instance));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('EngineEdition') AS INT), CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS NVARCHAR(400))", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var edition = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var folder = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("No se encontró la carpeta de respaldos de SQL Server (InstanceDefaultBackupPath).");
        return new Prepared(instance, folder, edition != 4);
    }

    /// <summary>differential_base_guid del archivo principal: cambia con cada copia completa (de Centinela o de otro).</summary>
    public static async Task<string?> DifferentialBaseAsync(string instance, string database, CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString(instance));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT CAST(differential_base_guid AS NVARCHAR(40)) FROM sys.master_files WHERE database_id = DB_ID(@db) AND file_id = 1", connection);
        command.Parameters.AddWithValue("@db", database);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// BACKUP DATABASE al archivo indicado (en la carpeta de SQL Server). completo: normal (base de los diferenciales) o
    /// COPY_ONLY (no toca la cadena de otros programas); diferencial: lo cambiado desde la última completa.
    /// </summary>
    public static async Task BackupAsync(Prepared sql, string database, string file, string kind, bool copyOnly, bool verify, CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString(sql.Instance));
        await connection.OpenAsync(ct);
        var options = new List<string> { "INIT", "CHECKSUM", "NAME = @name" };
        if (kind == "diferencial")
            options.Insert(0, "DIFFERENTIAL");
        else if (copyOnly)
            options.Insert(0, "COPY_ONLY");
        var name = $"Centinela {kind} {DateTime.Now:yyyy-MM-dd HH:mm}";
        try
        {
            await RunAsync(connection, $"BACKUP DATABASE {Quote(database)} TO DISK = @file WITH {string.Join(", ", sql.Compression ? options.Append("COMPRESSION") : options)}",
                file, name, ct);
        }
        catch (SqlException ex) when (sql.Compression && ex.Number == 1844)
        {
            // Esta edición no comprime (Web, por ejemplo): sin compresión.
            await RunAsync(connection, $"BACKUP DATABASE {Quote(database)} TO DISK = @file WITH {string.Join(", ", options)}", file, name, ct);
        }
        if (verify)
            await RunAsync(connection, "RESTORE VERIFYONLY FROM DISK = @file WITH CHECKSUM", file, null, ct);
    }

    private static async Task RunAsync(SqlConnection connection, string sql, string file, string? name, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@file", file);
        if (name is not null)
            command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync(ct);
    }
}

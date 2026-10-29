using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Interceptors;

namespace MonitoreoTI.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Recrea la base MonitoreoTI_Tests desde los scripts de database/ (00 a 03) una vez por corrida.
/// Requiere la cadena de conexión al servidor (sin base) en:
///   dotnet user-secrets set "ConnectionStrings:TestServer" "..."   (proyecto de pruebas)
/// o en la variable de entorno ConnectionStrings__TestServer.
/// </summary>
public sealed partial class TestDatabase : IAsyncLifetime
{
    public const string DatabaseName = "MonitoreoTI_Tests";

    public static string? ServerConnectionString { get; } = new ConfigurationBuilder()
        .AddUserSecrets<TestDatabase>(optional: true)
        .AddEnvironmentVariables()
        .Build()
        .GetConnectionString("TestServer");

    public string ConnectionString => WithDatabase(DatabaseName);

    public async Task InitializeAsync()
    {
        if (ServerConnectionString is null)
            return;

        var scripts = FindDatabaseFolder();

        await using (var master = new SqlConnection(WithDatabase("master")))
        {
            await master.OpenAsync();
            await ExecuteAsync(master, $"""
                IF DB_ID(N'{DatabaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{DatabaseName}];
                END
                """);
            var create = await File.ReadAllTextAsync(Path.Combine(scripts, "00_create_database.sql"));
            await RunScriptAsync(master, create.Replace("$(DbName)", DatabaseName));
        }

        SqlConnection.ClearAllPools();

        await using var database = new SqlConnection(ConnectionString);
        await database.OpenAsync();
        foreach (var script in new[] { "01_schema.sql", "02_multitenant.sql", "03_seed_dev.sql" })
            await RunScriptAsync(database, await File.ReadAllTextAsync(Path.Combine(scripts, script)));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public MonitoreoDbContext CreateContext(Action<TenantContext>? configureTenant = null)
    {
        var tenant = new TenantContext();
        configureTenant?.Invoke(tenant);

        var options = new DbContextOptionsBuilder<MonitoreoDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(new TenantSessionContextInterceptor(tenant))
            .Options;

        return new MonitoreoDbContext(options, tenant);
    }

    /// <summary>Conexión ADO.NET abierta, opcionalmente con el contexto de sesión de RLS fijado.</summary>
    public async Task<SqlConnection> OpenConnectionAsync(int? companyId = null, bool superAdmin = false)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        if (superAdmin)
            await ExecuteAsync(connection, "EXEC sp_set_session_context N'es_superadmin', 1, @read_only = 1;");
        else if (companyId is not null)
            await ExecuteAsync(connection, $"EXEC sp_set_session_context N'empresa_id', {companyId.Value}, @read_only = 1;");

        return connection;
    }

    public async Task<int> GetCompanyIdAsync(string name)
    {
        await using var connection = await OpenConnectionAsync(superAdmin: true);
        await using var command = new SqlCommand("SELECT id FROM dbo.empresas WHERE nombre = @nombre", connection);
        command.Parameters.AddWithValue("@nombre", name);
        return (int)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException($"No existe la empresa {name}."));
    }

    public static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    public static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RunScriptAsync(SqlConnection connection, string script)
    {
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await ExecuteAsync(connection, batch);
    }

    private static string WithDatabase(string database) =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = database }.ConnectionString;

    private static string FindDatabaseFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "database");
            if (File.Exists(Path.Combine(candidate, "01_schema.sql")))
                return candidate;
        }
        throw new DirectoryNotFoundException("No se encontró la carpeta database/ con los scripts SQL.");
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<TestDatabase>
{
    public const string Name = "database";
}

/// <summary>Prueba que necesita SQL Server; se omite si no hay cadena de conexión configurada.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (TestDatabase.ServerConnectionString is null)
            Skip = "Configure ConnectionStrings:TestServer (user-secrets del proyecto de pruebas) para ejecutar las pruebas contra SQL Server.";
    }
}

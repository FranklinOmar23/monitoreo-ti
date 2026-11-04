using System.Reflection;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;

namespace MonitoreoTI.Api.IntegrationTests.Persistence;

/// <summary>
/// El mapeo de EF Core es database-first: si una entidad, columna o valor de enum no coincide
/// con lo que crean los scripts, estas pruebas fallan.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ModelMatchesDatabaseTests(TestDatabase database)
{
    private static readonly MethodInfo ReadSampleMethod =
        typeof(ModelMatchesDatabaseTests).GetMethod(nameof(ReadSampleAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    [DatabaseFact]
    public async Task Every_mapped_entity_can_be_read_from_the_seeded_database()
    {
        await using var db = database.CreateContext(t => t.SetSuperAdmin());
        var failures = new List<string>();
        var rowsRead = 0;

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            try
            {
                var task = (Task<int>)ReadSampleMethod.MakeGenericMethod(entityType.ClrType).Invoke(null, [db])!;
                rowsRead += await task;
            }
            catch (Exception ex)
            {
                failures.Add($"{entityType.ClrType.Name}: {ex.GetBaseException().Message}");
            }
        }

        failures.Should().BeEmpty();
        rowsRead.Should().BeGreaterThan(0);
    }

    [DatabaseFact]
    public async Task Every_table_in_the_database_is_mapped()
    {
        await using var db = database.CreateContext(t => t.SetSuperAdmin());
        var mapped = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = new SqlCommand("SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        tables.Should().HaveCount(50);
        tables.Where(t => !mapped.Contains(t)).Should().BeEmpty("cada tabla del script debe tener su entidad");
    }

    [DatabaseFact]
    public async Task Seeded_values_are_converted_to_domain_enums()
    {
        await using var db = database.CreateContext(t => t.SetSuperAdmin());

        var statuses = await db.Machines.Select(m => m.Status).Distinct().ToListAsync();

        statuses.Should().Contain([
            Domain.Enums.MachineStatus.Healthy, Domain.Enums.MachineStatus.Warning,
            Domain.Enums.MachineStatus.Critical, Domain.Enums.MachineStatus.Disconnected]);
    }

    private static async Task<int> ReadSampleAsync<T>(DbContext db) where T : class =>
        (await db.Set<T>().AsNoTracking().Take(20).ToListAsync()).Count;
}

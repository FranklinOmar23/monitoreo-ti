using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Api.IntegrationTests.Infrastructure;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Api.IntegrationTests.Persistence;

/// <summary>
/// Aislamiento entre empresas en las capas de datos: Row-Level Security, FK compuestas
/// y el filtro global y las reglas de escritura de EF Core.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TenantIsolationTests(TestDatabase database)
{
    private const string Caribe = "Grupo Comercial Caribe";
    private const string SanRafael = "Clínica San Rafael";

    [DatabaseFact]
    public async Task Security_policy_covers_every_table_with_empresa_id()
    {
        await using var connection = await database.OpenConnectionAsync();

        var uncovered = await ListAsync(connection, """
            SELECT t.name
            FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id AND c.name = N'empresa_id'
            WHERE t.schema_id = SCHEMA_ID(N'dbo')
              AND t.name NOT IN (N'usuario_empresa', N'refresh_tokens')
              AND NOT EXISTS (
                    SELECT 1 FROM sys.security_predicates p
                    WHERE p.target_object_id = t.object_id AND p.predicate_type_desc = N'FILTER')
            """);

        uncovered.Should().BeEmpty("toda tabla con empresa_id debe estar en seguridad.politica_empresa");
    }

    [DatabaseFact]
    public async Task Without_session_context_business_tables_look_empty()
    {
        await using var connection = await database.OpenConnectionAsync();

        (await TestDatabase.ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.maquinas")).Should().Be(0);
        (await TestDatabase.ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.empresas")).Should().Be(0);
    }

    [DatabaseFact]
    public async Task Each_company_only_sees_its_own_machines()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);

        await using var caribeDb = database.CreateContext(t => t.SetCompany(caribe));
        await using var rafaelDb = database.CreateContext(t => t.SetCompany(rafael));

        var caribeMachines = await caribeDb.Machines.ToListAsync();
        var rafaelMachines = await rafaelDb.Machines.ToListAsync();

        caribeMachines.Should().HaveCount(6).And.OnlyContain(m => m.CompanyId == caribe);
        rafaelMachines.Should().ContainSingle().Which.CompanyId.Should().Be(rafael);
    }

    [DatabaseFact]
    public async Task Row_level_security_still_isolates_when_the_ef_filter_is_ignored()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);

        await using var db = database.CreateContext(t => t.SetCompany(caribe));

        var leaked = await db.Machines.IgnoreQueryFilters().CountAsync(m => m.CompanyId == rafael);

        leaked.Should().Be(0);
    }

    [DatabaseFact]
    public async Task Global_catalog_rows_are_visible_to_every_company()
    {
        var rafael = await database.GetCompanyIdAsync(SanRafael);
        await using var db = database.CreateContext(t => t.SetCompany(rafael));

        (await db.Roles.CountAsync(r => r.CompanyId == null)).Should().Be(5);
        (await db.DeviceModels.CountAsync(m => m.CompanyId == null)).Should().BeGreaterThan(0);
        (await db.Companies.ToListAsync()).Should().ContainSingle().Which.Id.Should().Be(rafael);
    }

    [DatabaseFact]
    public async Task Server_assigns_the_company_when_inserting()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        await using var db = database.CreateContext(t => t.SetCompany(caribe));

        // Los departamentos son de una sucursal (decisión 79).
        var branch = await db.Branches.Select(b => b.Id).FirstAsync();
        var department = new Department { BranchId = branch, Name = $"Prueba {Guid.NewGuid():N}" };
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        department.CompanyId.Should().Be(caribe);

        db.Departments.Remove(department);
        await db.SaveChangesAsync();
    }

    [DatabaseFact]
    public async Task Writing_a_record_for_another_company_is_rejected_by_ef()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);
        await using var db = database.CreateContext(t => t.SetCompany(caribe));

        db.Departments.Add(new Department { CompanyId = rafael, Name = "Intruso" });

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<TenantViolationException>();
    }

    [DatabaseFact]
    public async Task Changing_the_company_of_an_existing_record_is_rejected()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);
        await using var db = database.CreateContext(t => t.SetCompany(caribe));

        var machine = await db.Machines.FirstAsync();
        machine.CompanyId = rafael;

        // En entidades con clave (empresa_id, id) lo bloquea EF Core por ser parte de una clave;
        // en las demás, TenantViolationException. Ambas derivan de InvalidOperationException.
        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<InvalidOperationException>();
        await using var fresh = database.CreateContext(t => t.SetCompany(caribe));
        (await fresh.Machines.CountAsync()).Should().Be(6);
    }

    [DatabaseFact]
    public async Task Database_blocks_writes_for_another_company_without_ef()
    {
        int rafaelBranch;
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);
        await using (var admin = database.CreateContext(t => t.SetSuperAdmin()))
            rafaelBranch = await admin.Branches.Where(b => b.CompanyId == rafael).Select(b => b.Id).FirstAsync();
        await using var connection = await database.OpenConnectionAsync(companyId: caribe);

        var insert = () => TestDatabase.ExecuteAsync(connection,
            $"INSERT INTO dbo.departamentos (empresa_id, sucursal_id, nombre) VALUES ({rafael}, {rafaelBranch}, N'Intruso');");

        await insert.Should().ThrowAsync<SqlException>().Where(e => e.Number == 33504);
    }

    [DatabaseFact]
    public async Task Composite_foreign_keys_reject_cross_company_references()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);
        await using var connection = await database.OpenConnectionAsync(superAdmin: true);
        var rafaelBranch = (int)(await TestDatabase.ScalarAsync(connection,
            $"SELECT TOP 1 id FROM dbo.sucursales WHERE empresa_id = {rafael}"))!;

        var insert = () => TestDatabase.ExecuteAsync(connection,
            $"INSERT INTO dbo.maquinas (empresa_id, sucursal_id, nombre) VALUES ({caribe}, {rafaelBranch}, N'CRUZADA');");

        await insert.Should().ThrowAsync<SqlException>().Where(e => e.Number == 547);
    }

    [DatabaseFact]
    public async Task Metrics_purge_requires_matching_session_context()
    {
        var caribe = await database.GetCompanyIdAsync(Caribe);
        var rafael = await database.GetCompanyIdAsync(SanRafael);
        await using var connection = await database.OpenConnectionAsync(companyId: caribe);

        var wrongCompany = () => TestDatabase.ExecuteAsync(connection,
            $"EXEC dbo.sp_resumir_y_purgar_metricas @empresa_id = {rafael};");

        await wrongCompany.Should().ThrowAsync<SqlException>().Where(e => e.Number == 50001);
        await TestDatabase.ExecuteAsync(connection, $"EXEC dbo.sp_resumir_y_purgar_metricas @empresa_id = {caribe};");
    }

    private static async Task<List<string>> ListAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}

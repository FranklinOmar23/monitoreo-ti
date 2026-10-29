using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Agents;

/// <summary>
/// Singleton: la conexión de un agente dura horas, así que cada operación abre su propio ámbito
/// con el contexto de su empresa (la conexión de SQL Server queda ligada a esa empresa por RLS).
/// Los cambios de estado se avisan en vivo al panel.
/// </summary>
public sealed class AgentSessions(IServiceScopeFactory scopes, IRealtimeNotifier notifier) : IAgentSessions
{
    public async Task<AgentIdentity?> AuthenticateAsync(string? credential, CancellationToken cancellationToken)
    {
        if (!AgentTokens.TryGetCompanyId(credential, out var companyId))
            return null;

        await using var scope = scopes.CreateAsyncScope();
        var db = OpenCompanyScope(scope, companyId);
        var hash = AgentTokens.Hash(credential!);

        // Una empresa suspendida o cancelada pierde la conexión de todos sus agentes;
        // un agente pendiente de aprobación (W-19) espera hasta que lo aprueben.
        var row = await db.Machines.AsNoTracking()
            .Where(m => m.Agent != null && m.Agent.TokenHash == hash && !m.Agent.IsRevoked && m.Agent.IsApproved
                && m.Status != MachineStatus.Retired)
            .Where(m => db.Companies.Any(c => c.Id == m.CompanyId && c.Status == CompanyStatus.Active))
            .Select(m => new { AgentId = m.Agent!.Id, MachineId = m.Id, m.Agent.IsCollector })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : new AgentIdentity(companyId, row.AgentId, row.MachineId, row.IsCollector);
    }

    public async Task OnConnectedAsync(AgentIdentity identity, HolaData hola, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = OpenCompanyScope(scope, identity.CompanyId);
        var now = DateTime.UtcNow;

        await db.Agents.Where(a => a.Id == identity.AgentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Version, hola.Version)
                .SetProperty(a => a.LastContactAt, now), cancellationToken);

        // Un equipo que vuelve a conectarse deja de estar "desconectado"; el motor de alertas (Fase 3) afina el semáforo.
        var machine = await db.Machines.FirstAsync(m => m.Id == identity.MachineId, cancellationToken);
        machine.LastReportAt = now;
        var cameBack = machine.Status == MachineStatus.Disconnected;
        if (cameBack)
            machine.Status = MachineStatus.Healthy;
        await db.SaveChangesAsync(cancellationToken);

        if (cameBack)
        {
            await notifier.MachineStatusChangedAsync(identity.CompanyId, machine.BranchId, machine.Id,
                PanelQueries.Text(MachineStatus.Healthy), cancellationToken);
        }
    }

    public async Task FlushLastSeenAsync(IReadOnlyCollection<AgentPresenceEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var group in entries.GroupBy(e => e.Identity.CompanyId))
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = OpenCompanyScope(scope, group.Key);

            foreach (var entry in group)
            {
                var seen = entry.LastSeenUtc;
                await db.Agents.Where(a => a.Id == entry.Identity.AgentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastContactAt, seen), cancellationToken);
                await db.Machines.Where(m => m.Id == entry.Identity.MachineId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReportAt, seen), cancellationToken);
            }
        }
    }

    public async Task<int> MarkStaleDisconnectedAsync(int companyId, DateTime threshold, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = OpenCompanyScope(scope, companyId);

        // Solo equipos con agente: las impresoras y equipos manuales no reportan y no deben pasar a "desconectado".
        var stale = await db.Machines
            .Where(m => m.Status != MachineStatus.Disconnected && m.Status != MachineStatus.Retired)
            .Where(m => m.Agent != null && m.Agent.LastContactAt != null && m.Agent.LastContactAt < threshold)
            .ToListAsync(cancellationToken);

        foreach (var machine in stale)
            machine.Status = MachineStatus.Disconnected;
        await db.SaveChangesAsync(cancellationToken);

        var disconnected = PanelQueries.Text(MachineStatus.Disconnected);
        foreach (var machine in stale)
            await notifier.MachineStatusChangedAsync(companyId, machine.BranchId, machine.Id, disconnected, cancellationToken);

        return stale.Count;
    }

    public async Task SnapshotDailyStatusAsync(int companyId, DateOnly date, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = OpenCompanyScope(scope, companyId);

        // Una fila por empresa y día: se reescribe durante el día y el último valor queda como resumen.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            MERGE dbo.estado_diario WITH (HOLDLOCK) AS d
            USING (
                SELECT {companyId} AS empresa_id, CAST({date.ToDateTime(TimeOnly.MinValue)} AS DATE) AS fecha,
                       SUM(CASE WHEN estado = 'sano' THEN 1 ELSE 0 END) AS sano,
                       SUM(CASE WHEN estado = 'advertencia' THEN 1 ELSE 0 END) AS advertencia,
                       SUM(CASE WHEN estado = 'critico' THEN 1 ELSE 0 END) AS critico,
                       SUM(CASE WHEN estado = 'desconectado' THEN 1 ELSE 0 END) AS desconectado
                FROM dbo.maquinas WHERE empresa_id = {companyId} AND estado <> 'baja'
            ) AS s
            ON d.empresa_id = s.empresa_id AND d.fecha = s.fecha
            WHEN MATCHED THEN UPDATE SET sano = ISNULL(s.sano, 0), advertencia = ISNULL(s.advertencia, 0),
                critico = ISNULL(s.critico, 0), desconectado = ISNULL(s.desconectado, 0), actualizado = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (empresa_id, fecha, sano, advertencia, critico, desconectado)
                VALUES (s.empresa_id, s.fecha, ISNULL(s.sano, 0), ISNULL(s.advertencia, 0), ISNULL(s.critico, 0), ISNULL(s.desconectado, 0));
            """, cancellationToken);
    }

    public async Task<IReadOnlyList<int>> ListCompanyIdsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetSuperAdmin();
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        return await db.Companies.AsNoTracking()
            .Where(c => c.Status == CompanyStatus.Active)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    private static MonitoreoDbContext OpenCompanyScope(AsyncServiceScope scope, int companyId)
    {
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        return scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
    }
}

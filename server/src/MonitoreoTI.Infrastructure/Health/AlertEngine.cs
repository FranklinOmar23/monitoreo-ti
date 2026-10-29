using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;

namespace MonitoreoTI.Infrastructure.Health;

/// <summary>Resultado de evaluar una empresa (para el registro y las pruebas).</summary>
public sealed record AlertEngineResult(int Opened, int Updated, int Resolved, int StatusChanges);

/// <summary>
/// Motor de alertas (sección 7.5): evalúa los equipos de una empresa con sus reglas, abre una alerta por problema
/// nuevo, actualiza las que cambian y resuelve solas las que desaparecen. Después ajusta el semáforo de cada equipo
/// según sus alertas abiertas y avisa al panel en vivo. Solo toca alertas con código (las que crea él).
/// </summary>
public sealed class AlertEngine(IServiceScopeFactory scopes, IRealtimeNotifier notifier, TimeProvider clock)
{
    /// <param name="onlyMachineIds">Para evaluar solo esos equipos; null = todos los equipos con agente.</param>
    public async Task<AlertEngineResult> EvaluateCompanyAsync(
        int companyId, CancellationToken cancellationToken, IReadOnlyCollection<int>? onlyMachineIds = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;

        var rules = await LoadRulesAsync(db, cancellationToken);
        var query = db.Machines.Where(m => m.Agent != null && m.Status != MachineStatus.Retired);
        if (onlyMachineIds is not null)
            query = query.Where(m => onlyMachineIds.Contains(m.Id));
        var machines = await query.ToListAsync(cancellationToken);
        if (machines.Count == 0)
            return new AlertEngineResult(0, 0, 0, 0);

        var ids = machines.Select(m => m.Id).ToList();
        var since30 = now.AddMinutes(-30);
        var since24h = now.AddHours(-24);

        var volumes = await db.Volumes.AsNoTracking().Where(v => ids.Contains(v.MachineId)).ToListAsync(cancellationToken);
        var metrics = await db.Metrics.AsNoTracking().Where(m => ids.Contains(m.MachineId) && m.Timestamp >= since30)
            .Select(m => new { m.MachineId, m.Timestamp, m.Cpu, m.Ram, m.Temperature }).ToListAsync(cancellationToken);
        var events = await db.ErrorEvents.AsNoTracking()
            .Where(e => ids.Contains(e.MachineId) && e.Timestamp >= since24h && e.Level == EventLevel.Critical)
            .Select(e => new { e.MachineId, e.Timestamp, e.Source, e.Code }).ToListAsync(cancellationToken);
        var services = await db.Services.AsNoTracking().Where(s => ids.Contains(s.MachineId) && s.StoppedSince != null)
            .Select(s => new { s.MachineId, s.Name, s.DisplayName }).ToListAsync(cancellationToken);
        var components = await db.Components.AsNoTracking()
            .Where(c => ids.Contains(c.MachineId) && (c.Type == "disco" || c.Type == "antivirus"))
            .Select(c => new { c.MachineId, c.Type, c.Description, c.HealthStatus, c.Detail }).ToListAsync(cancellationToken);
        var open = await db.Alerts.Where(a => a.MachineId != null && ids.Contains(a.MachineId.Value) && a.Status != AlertStatus.Resolved)
            .ToListAsync(cancellationToken);

        int opened = 0, updated = 0, resolved = 0;
        var touched = new HashSet<int>();
        var changed = new List<(Alert Alert, int BranchId, bool IsNew)>();

        foreach (var machine in machines)
        {
            var machineRules = rules.For(machine.BranchId);
            var snapshot = new MachineSnapshot(
                machine.Id,
                machine.Name,
                machine.Status == MachineStatus.Disconnected,
                machine.LastReportAt,
                volumes.Where(v => v.MachineId == machine.Id).Select(v => new VolumeSnapshot(v.Drive, v.TotalGb, v.FreeGb)).ToList(),
                metrics.Where(m => m.MachineId == machine.Id).Select(m => new MetricSample(m.Timestamp, m.Cpu, m.Ram, m.Temperature)).ToList(),
                events.Where(e => e.MachineId == machine.Id).Select(e => new EventSnapshot(e.Timestamp, e.Source, e.Code)).ToList(),
                services.Where(s => s.MachineId == machine.Id).Select(s => new StoppedService(s.Name, s.DisplayName)).ToList(),
                components.Where(c => c.MachineId == machine.Id)
                    .Select(c => new ComponentSnapshot(c.Type, c.Description, EnumMemberConverter<HealthStatus>.ToProvider(c.HealthStatus), c.Detail))
                    .ToList());

            var findings = HealthRules.Evaluate(snapshot, machineRules.Set, now);
            var machineOpen = open.Where(a => a.MachineId == machine.Id && a.Code != null).ToList();

            foreach (var finding in findings)
            {
                var level = finding.Level == FindingLevel.Critical ? AlertLevel.Critical : AlertLevel.Warning;
                var alert = machineOpen.FirstOrDefault(a => a.Code == finding.Code);
                if (alert is null)
                {
                    alert = new Alert
                    {
                        MachineId = machine.Id,
                        Code = Cut(finding.Code, 80),
                        Status = AlertStatus.New,
                    };
                    db.Alerts.Add(alert);
                    open.Add(alert);
                    opened++;
                    touched.Add(machine.Id);
                    changed.Add((alert, machine.BranchId, true));
                }
                else if (alert.Level != level || alert.Message != Cut(finding.Message, 500))
                {
                    updated++;
                    touched.Add(machine.Id);
                    changed.Add((alert, machine.BranchId, false));
                }

                alert.RuleId = finding.Metric is null ? null : machineRules.Ids.GetValueOrDefault(finding.Metric);

                alert.Level = level;
                alert.Message = Cut(finding.Message, 500)!;
                alert.Detail = Cut(finding.Detail, 500);
                alert.Advice = Cut(finding.Advice, 500);
                alert.Source = Cut(finding.Source, 200);
            }

            // Lo que ya no se encuentra se resuelve solo (criterio de aceptación de la Fase 3).
            foreach (var alert in machineOpen.Where(a => findings.All(f => f.Code != a.Code)))
            {
                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = now;
                resolved++;
                touched.Add(machine.Id);
                changed.Add((alert, machine.BranchId, false));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (alert, branchId, isNew) in changed)
            await notifier.AlertChangedAsync(companyId, branchId, alert.Id, alert.MachineId, isNew, cancellationToken);

        // Semáforo: el peor nivel entre las alertas abiertas del equipo (incluidas las creadas a mano).
        var statusChanges = new List<(int BranchId, int MachineId, MachineStatus Status)>();
        foreach (var machine in machines.Where(m => m.Status is not MachineStatus.Disconnected and not MachineStatus.Retired))
        {
            var levels = open.Where(a => a.MachineId == machine.Id && a.Status != AlertStatus.Resolved).Select(a => a.Level).ToList();
            var status = levels.Contains(AlertLevel.Critical) ? MachineStatus.Critical
                : levels.Contains(AlertLevel.Warning) ? MachineStatus.Warning
                : MachineStatus.Healthy;
            if (machine.Status != status)
            {
                machine.Status = status;
                statusChanges.Add((machine.BranchId, machine.Id, status));
            }
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (branchId, machineId, status) in statusChanges)
            await notifier.MachineStatusChangedAsync(companyId, branchId, machineId, PanelQueries.Text(status), cancellationToken);
        foreach (var machineId in touched.Except(statusChanges.Select(s => s.MachineId)))
        {
            var machine = machines.First(m => m.Id == machineId);
            await notifier.MachineStatusChangedAsync(companyId, machine.BranchId, machineId, PanelQueries.Text(machine.Status), cancellationToken);
        }

        return new AlertEngineResult(opened, updated, resolved, statusChanges.Count);
    }

    private sealed record MachineRules(RuleSet Set, Dictionary<string, int> Ids);

    /// <summary>Reglas de la empresa: para cada sucursal, la de la sucursal; si no hay, la de la empresa; si no, la sección 14.</summary>
    private sealed class CompanyRules(List<AlertRule> rows)
    {
        private readonly Dictionary<int, MachineRules> _byBranch = [];

        public MachineRules For(int branchId)
        {
            if (_byBranch.TryGetValue(branchId, out var cached))
                return cached;

            var thresholds = new Dictionary<string, RuleThreshold>();
            var ids = new Dictionary<string, int>();
            // Primero las de la empresa; las de la sucursal las reemplazan.
            foreach (var rule in rows.Where(r => r.BranchId is null).Concat(rows.Where(r => r.BranchId == branchId)))
            {
                var metric = EnumMemberConverter<AlertMetric>.ToProvider(rule.Metric);
                thresholds[metric] = new RuleThreshold(rule.WarningThreshold, rule.CriticalThreshold, rule.DurationMinutes, rule.IsActive);
                ids[metric] = rule.Id;
            }
            return _byBranch[branchId] = new MachineRules(new RuleSet(thresholds), ids);
        }
    }

    private static async Task<CompanyRules> LoadRulesAsync(MonitoreoDbContext db, CancellationToken cancellationToken) =>
        new(await db.AlertRules.AsNoTracking().ToListAsync(cancellationToken));

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

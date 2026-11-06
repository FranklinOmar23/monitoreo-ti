using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Health;

/// <summary>
/// Alertas de red (sección 14): switch sin responder SNMP, errores en un puerto y puerto de un equipo conocido caído.
/// Igual que el motor de equipos: una alerta abierta por código, que se resuelve sola cuando el problema desaparece.
/// Después ajusta el estado del dispositivo (en línea, advertencia, crítico o sin respuesta).
/// </summary>
public sealed class NetworkAlertEngine(IServiceScopeFactory scopes, IRealtimeNotifier notifier, TimeProvider clock)
{
    public async Task<AlertEngineResult> EvaluateCompanyAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
        var now = clock.GetUtcNow().UtcDateTime;

        var devices = await db.NetworkDevices
            .Where(d => d.Status != NetworkDeviceStatus.Retired && d.Status != NetworkDeviceStatus.Passive)
            .ToListAsync(cancellationToken);
        var ids = devices.Select(d => d.Id).ToList();
        var open = await db.Alerts.Where(a => a.DeviceId != null && ids.Contains(a.DeviceId.Value) && a.Status != AlertStatus.Resolved)
            .ToListAsync(cancellationToken);
        if (devices.Count == 0 && open.Count == 0)
            return new AlertEngineResult(0, 0, 0, 0);

        var ruleRows = await db.AlertRules.AsNoTracking().ToListAsync(cancellationToken);
        var ports = await db.Ports.AsNoTracking().Where(p => ids.Contains(p.DeviceId))
            .Select(p => new { p.Id, p.DeviceId, p.Name, p.Number, p.Status }).ToListAsync(cancellationToken);
        var portIds = ports.Select(p => p.Id).ToList();
        var since = now.AddHours(-2);
        var metrics = (await db.PortMetrics.AsNoTracking().Where(m => portIds.Contains(m.PortId) && m.Timestamp >= since)
                .Select(m => new { m.PortId, m.Timestamp, m.ErrorsIn, m.ErrorsOut }).ToListAsync(cancellationToken))
            .GroupBy(m => m.PortId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Timestamp).Take(2).ToList());
        var knownSince = now.AddHours(-24);
        var known = (await db.MacTable.AsNoTracking().Where(m => portIds.Contains(m.PortId) && m.MachineId != null && m.LastSeenAt >= knownSince)
                .Select(m => new { m.PortId, m.MachineId, m.LastSeenAt, Name = db.Machines.Where(x => x.Id == m.MachineId).Select(x => x.Name).First() })
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.PortId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.LastSeenAt).First());

        int opened = 0, updated = 0, resolved = 0, statusChanges = 0;
        var changed = new List<(Alert Alert, int BranchId, bool IsNew)>();
        var deviceChanges = new List<(int BranchId, int DeviceId, string Status)>();

        foreach (var device in devices)
        {
            var (rules, ruleIds) = RulesFor(ruleRows, device.BranchId);
            // Por SNMP, o un FortiSwitch cuyos datos trae su FortiGate (decisión 80).
            var polled = (device.CollectorAgentId != null && device.ManagementIp != null && device.SnmpEncryptedCredential != null)
                || device.FortiGateId != null;
            var patience = TimeSpan.FromMinutes((double)(rules.For("switch_sin_respuesta")?.Warning ?? 10));
            var responding = device.LastError is null && device.LastPolledAt is DateTime polledAt && now - polledAt < patience;

            var snapshot = new DeviceSnapshot(
                device.Id, device.Name, polled, device.LastPolledAt ?? device.CreatedAt, responding, device.LastError,
                ports.Where(p => p.DeviceId == device.Id).Select(p =>
                {
                    var samples = metrics.GetValueOrDefault(p.Id);
                    decimal? rate = samples is { Count: 2 }
                        ? SnmpRules.ErrorsPerHour(Sum(samples[1].ErrorsIn, samples[1].ErrorsOut), samples[1].Timestamp, Sum(samples[0].ErrorsIn, samples[0].ErrorsOut), samples[0].Timestamp)
                        : null;
                    return new PortSnapshot(p.Id, $"{device.Name} {p.Name ?? p.Number.ToString()}", Text(p.Status), rate, known.GetValueOrDefault(p.Id)?.Name);
                }).ToList());

            var findings = NetworkHealth.Evaluate(snapshot, rules, now);
            var deviceOpen = open.Where(a => a.DeviceId == device.Id && a.Code != null).ToList();
            foreach (var finding in findings)
            {
                var level = finding.Level == FindingLevel.Critical ? AlertLevel.Critical : AlertLevel.Warning;
                var portId = PortOf(finding.Code);
                var alert = deviceOpen.FirstOrDefault(a => a.Code == finding.Code);
                if (alert is null)
                {
                    alert = new Alert { DeviceId = device.Id, PortId = portId, Code = finding.Code, Status = AlertStatus.New };
                    // El puerto caído de un equipo conocido también aparece en la ficha de ese equipo.
                    if (portId is int p && finding.Metric == "puerto_caido")
                        alert.MachineId = known.GetValueOrDefault(p)?.MachineId;
                    db.Alerts.Add(alert);
                    open.Add(alert);
                    opened++;
                    changed.Add((alert, device.BranchId, true));
                }
                else if (alert.Level != level || alert.Message != finding.Message)
                {
                    updated++;
                    changed.Add((alert, device.BranchId, false));
                }
                alert.RuleId = finding.Metric is null ? null : ruleIds.GetValueOrDefault(finding.Metric);
                alert.Level = level;
                alert.Message = Cut(finding.Message, 500)!;
                alert.Detail = Cut(finding.Detail, 500);
                alert.Advice = Cut(finding.Advice, 500);
                alert.Source = Cut(finding.Source, 200);
            }
            foreach (var alert in deviceOpen.Where(a => findings.All(f => f.Code != a.Code)))
            {
                alert.Status = AlertStatus.Resolved;
                alert.ResolvedAt = now;
                resolved++;
                changed.Add((alert, device.BranchId, false));
            }

            if (!polled)
                continue;
            var levels = open.Where(a => a.DeviceId == device.Id && a.Status != AlertStatus.Resolved).Select(a => a.Level).ToList();
            var status = !responding ? NetworkDeviceStatus.NotResponding
                : levels.Contains(AlertLevel.Critical) ? NetworkDeviceStatus.Critical
                : levels.Contains(AlertLevel.Warning) ? NetworkDeviceStatus.Warning
                : NetworkDeviceStatus.Online;
            if (device.Status != status)
            {
                device.Status = status;
                statusChanges++;
                deviceChanges.Add((device.BranchId, device.Id, Text(status)));
            }
        }

        // Alertas de dispositivos dados de baja o pasados a pasivos: ya no hay nada que vigilar.
        foreach (var alert in open.Where(a => a.Status != AlertStatus.Resolved && a.DeviceId is int d && !ids.Contains(d)))
        {
            alert.Status = AlertStatus.Resolved;
            alert.ResolvedAt = now;
            resolved++;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var (alert, branchId, isNew) in changed)
            await notifier.AlertChangedAsync(companyId, branchId, alert.Id, alert.MachineId, isNew, cancellationToken);
        foreach (var (branchId, deviceId, status) in deviceChanges)
            await notifier.PortStatusChangedAsync(companyId, branchId, deviceId, null, status, cancellationToken);
        return new AlertEngineResult(opened, updated, resolved, statusChanges);
    }

    private static int? PortOf(string code) =>
        code.IndexOf(':') is var i and > 0 && int.TryParse(code[(i + 1)..], out var id) ? id : null;

    /// <summary>Para la sucursal: su regla; si no tiene, la de la empresa; si no, la sección 14.</summary>
    private static (RuleSet Rules, Dictionary<string, int> Ids) RulesFor(List<AlertRule> rows, int branchId)
    {
        var thresholds = new Dictionary<string, RuleThreshold>();
        var ids = new Dictionary<string, int>();
        foreach (var rule in rows.Where(r => r.BranchId is null).Concat(rows.Where(r => r.BranchId == branchId)))
        {
            var metric = EnumMemberConverter<AlertMetric>.ToProvider(rule.Metric);
            thresholds[metric] = new RuleThreshold(rule.WarningThreshold, rule.CriticalThreshold, rule.DurationMinutes, rule.IsActive);
            ids[metric] = rule.Id;
        }
        return (new RuleSet(thresholds), ids);
    }

    private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

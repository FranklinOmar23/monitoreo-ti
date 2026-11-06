using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Lecturas de alertas (W-20, W-21, W-54) y reglas (W-22). Un usuario con sucursales asignadas ve solo las alertas de
/// los equipos y de los dispositivos de red de esas sucursales.
/// </summary>
public sealed class AlertQueries(MonitoreoDbContext db, ICurrentUser user, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<IQueryable<Alert>> VisibleAlertsAsync(CancellationToken ct)
    {
        var branches = await user.AllowedBranchIdsAsync(ct);
        var alerts = db.Alerts.AsNoTracking();
        if (branches is null)
            return alerts;
        var machineIds = db.Machines.Where(m => branches.Contains(m.BranchId)).Select(m => m.Id);
        var deviceIds = db.NetworkDevices.Where(d => branches.Contains(d.BranchId)).Select(d => d.Id);
        return alerts.Where(a => a.DeviceId != null ? deviceIds.Contains(a.DeviceId.Value) : a.MachineId != null && machineIds.Contains(a.MachineId.Value));
    }

    public async Task<AlertList> AlertsAsync(AlertFilter filter, CancellationToken ct)
    {
        // Decisión 83: la vista trae el equipo o dispositivo, la sucursal y quién la atiende en la misma consulta.
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var scoped = db.AlertListView.AsNoTracking();
        if (allowed is not null)
            scoped = scoped.Where(a => a.BranchId != null && allowed.Contains(a.BranchId.Value));

        if (filter.BranchId is int branchId)
            scoped = scoped.Where(a => a.BranchId == branchId);
        if (filter.MachineId is int machineId)
            scoped = scoped.Where(a => a.MachineId == machineId);
        if (filter.DeviceId is int deviceId)
            scoped = scoped.Where(a => a.DeviceId == deviceId);
        if (filter.Network is bool network)
            scoped = network ? scoped.Where(a => a.DeviceId != null) : scoped.Where(a => a.DeviceId == null);
        if (filter.HandledById is int handledBy)
            scoped = scoped.Where(a => a.HandledById == handledBy);
        if (filter.Automatic is bool automatic)
            scoped = automatic ? scoped.Where(a => a.Code != null) : scoped.Where(a => a.Code == null);
        if (!string.IsNullOrWhiteSpace(filter.Source))
            scoped = scoped.Where(a => a.Source != null && a.Source.Contains(filter.Source.Trim()));
        if (!string.IsNullOrWhiteSpace(filter.Code))
            scoped = scoped.Where(a => a.Code != null && a.Code.Contains(filter.Code.Trim()));
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            scoped = scoped.Where(a => a.Message.Contains(q) || (a.Machine != null && a.Machine.Contains(q)) || (a.Device != null && a.Device.Contains(q)));
        }
        var (from, to) = Paging.DateRange(filter.From, filter.To);
        if (from is not null)
            scoped = scoped.Where(a => a.CreatedAt >= from);
        if (to is not null)
            scoped = scoped.Where(a => a.CreatedAt < to);

        var dayAgo = Now.AddDays(-1);
        var tally = await scoped
            .Where(a => a.Status != AlertStatus.Resolved || a.ResolvedAt >= dayAgo)
            .GroupBy(a => new { a.Status, a.Level })
            .Select(g => new { g.Key.Status, g.Key.Level, Count = g.Count() })
            .ToListAsync(ct);
        var open = tally.Where(t => t.Status != AlertStatus.Resolved).ToList();
        var counts = new AlertCounts(
            open.Sum(t => t.Count),
            open.Where(t => t.Status == AlertStatus.New).Sum(t => t.Count),
            open.Where(t => t.Status == AlertStatus.InProgress).Sum(t => t.Count),
            open.Where(t => t.Level == AlertLevel.Critical).Sum(t => t.Count),
            open.Where(t => t.Level == AlertLevel.Warning).Sum(t => t.Count),
            tally.Where(t => t.Status == AlertStatus.Resolved).Sum(t => t.Count));

        var query = (filter.Status ?? "abiertas") switch
        {
            "nueva" => scoped.Where(a => a.Status == AlertStatus.New),
            "en_proceso" => scoped.Where(a => a.Status == AlertStatus.InProgress),
            "resuelta" => scoped.Where(a => a.Status == AlertStatus.Resolved),
            "todas" => scoped,
            _ => scoped.Where(a => a.Status != AlertStatus.Resolved),
        };
        if (filter.Level is "advertencia" or "critico")
        {
            var level = filter.Level == "critico" ? AlertLevel.Critical : AlertLevel.Warning;
            query = query.Where(a => a.Level == level);
        }

        var (page, limit) = Paging.Normalize(filter.Page, filter.Limit);
        var rows = await query
            .OrderBy(a => a.IsResolved).ThenByDescending(a => a.LevelOrder).ThenByDescending(a => a.SortDate).ThenByDescending(a => a.Id)
            .ToPageAsync(page, limit, ct);

        var items = rows.Map(a => new AlertListItem(
            a.Id, Text(a.Level), Text(a.Status), a.Message, a.Source, a.MachineId, a.Machine, a.BranchId, a.Branch, a.HandledBy,
            a.CreatedAt, a.ResolvedAt, a.Code is not null, a.DeviceId, a.Device));
        return new AlertList(items, counts);
    }

    public async Task<AlertDetail?> AlertAsync(long id, CancellationToken ct)
    {
        var alert = await (await VisibleAlertsAsync(ct))
            .Where(a => a.Id == id)
            .Select(a => new
            {
                Alert = a,
                Machine = db.Machines.Where(m => m.Id == a.MachineId)
                    .Select(m => new { m.Name, m.Status, m.BranchId, Branch = m.Branch!.Name }).FirstOrDefault(),
                Rule = db.AlertRules.Where(r => r.Id == a.RuleId).Select(r => r.Name).FirstOrDefault(),
                Device = db.NetworkDevices.Where(d => d.Id == a.DeviceId)
                    .Select(d => new { d.Name, d.BranchId, Branch = db.Branches.Where(b => b.Id == d.BranchId).Select(b => b.Name).First() }).FirstOrDefault(),
                HandledBy = db.Users.Where(u => u.Id == a.HandledById).Select(u => u.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (alert is null)
            return null;

        var a = alert.Alert;
        var since = Now.AddDays(-30);
        var recurrences = a.Code is null || (a.MachineId is null && a.DeviceId is null)
            ? 0
            : await db.Alerts.CountAsync(x => x.MachineId == a.MachineId && x.DeviceId == a.DeviceId && x.Code == a.Code && x.Id != a.Id && x.CreatedAt >= since, ct);

        var entityId = id.ToString();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(l => l.Entity == "alertas" && l.EntityId == entityId)
            .OrderBy(l => l.Timestamp)
            .Select(l => new { l.Timestamp, l.Action, l.Details, User = db.Users.Where(u => u.Id == l.UserId).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);

        var history = new List<AlertHistoryItem>
        {
            new(a.CreatedAt, a.Code is null ? "Creada" : "Detectada por el sistema", null, null),
        };
        history.AddRange(audit.Select(l => new AlertHistoryItem(l.Timestamp, ActionLabel(l.Action), l.User, NoteOf(l.Details))));
        if (a.Status == AlertStatus.Resolved && a.ResolvedAt is { } resolvedAt && audit.All(l => l.Action != "alerta.resolver"))
            history.Add(new AlertHistoryItem(resolvedAt, "Resuelta automáticamente: el problema ya no se detecta", null, null));

        return new AlertDetail(
            a.Id, Text(a.Level), Text(a.Status), a.Message, a.Detail, a.Advice, a.Source, a.Code, a.Code is not null,
            a.MachineId, alert.Machine?.Name, alert.Machine is null ? null : Text(alert.Machine.Status),
            alert.Machine?.BranchId ?? alert.Device?.BranchId, alert.Machine?.Branch ?? alert.Device?.Branch, a.RuleId, alert.Rule, alert.HandledBy,
            a.CreatedAt, a.ResolvedAt, recurrences, history.OrderBy(h => h.At).ToList(), a.DeviceId, alert.Device?.Name, a.PortId);
    }

    private static string ActionLabel(string action) => action switch
    {
        "alerta.atender" => "En proceso",
        "alerta.resolver" => "Resuelta",
        _ => action,
    };

    /// <summary>La nota que dejó el técnico, guardada en el "después" de la auditoría.</summary>
    private static string? NoteOf(string? details)
    {
        if (details is null)
            return null;
        try
        {
            using var json = JsonDocument.Parse(details);
            return json.RootElement.TryGetProperty("despues", out var after) && after.ValueKind == JsonValueKind.Object
                && after.TryGetProperty("nota", out var note) && note.ValueKind == JsonValueKind.String
                ? note.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ reglas

    /// <summary>
    /// Cada métrica de la sección 14 con su regla para toda la empresa (o el valor por defecto si la empresa no la
    /// cambió) y los ajustes de las sucursales que el usuario puede ver.
    /// </summary>
    public async Task<IReadOnlyList<RuleItem>> RulesAsync(CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var rows = await db.AlertRules.AsNoTracking()
            .Where(r => r.BranchId == null || allowed == null || allowed.Contains(r.BranchId.Value))
            .Select(r => new { Rule = r, Branch = db.Branches.Where(b => b.Id == r.BranchId).Select(b => b.Name).FirstOrDefault() })
            .ToListAsync(ct);

        return RuleCatalog.All.Select(metric =>
        {
            var mine = rows.Where(r => EnumMemberConverter<AlertMetric>.ToProvider(r.Rule.Metric) == metric.Key).ToList();
            var company = mine.FirstOrDefault(r => r.Rule.BranchId is null)?.Rule;
            var fallback = RuleSet.Defaults.GetValueOrDefault(metric.Key) ?? new RuleThreshold(null, null);
            var overrides = mine.Where(r => r.Rule.BranchId is not null)
                .Select(r => new RuleOverride(r.Rule.Id, r.Rule.BranchId!.Value, r.Branch ?? "—", r.Rule.WarningThreshold,
                    r.Rule.CriticalThreshold, r.Rule.DurationMinutes, r.Rule.IsActive, r.Rule.AutoRepair))
                .OrderBy(o => o.BranchName)
                .ToList();

            return new RuleItem(
                company?.Id, metric.Key, metric.Name, metric.Description, metric.Unit, metric.LessIsWorse, metric.HasThresholds,
                metric.HasDuration, metric.Available,
                company is null ? fallback.Warning : company.WarningThreshold,
                company is null ? fallback.Critical : company.CriticalThreshold,
                company?.DurationMinutes ?? fallback.DurationMinutes,
                company?.IsActive ?? fallback.Active,
                overrides,
                company?.AutoRepair ?? false,
                metric.Key == "servicio_detenido");
        }).ToList();
    }
}

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
/// Lecturas de alertas (W-20, W-21) y reglas (W-22). Un usuario con sucursales asignadas ve solo las alertas de los
/// equipos de esas sucursales; las alertas sin equipo (red, Fase 4) solo las ve quien tiene acceso a todas.
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
        return alerts.Where(a => a.MachineId != null && machineIds.Contains(a.MachineId.Value));
    }

    public async Task<AlertList> AlertsAsync(AlertFilter filter, CancellationToken ct)
    {
        var scoped = await VisibleAlertsAsync(ct);

        if (filter.BranchId is int branchId)
            scoped = scoped.Where(a => db.Machines.Any(m => m.Id == a.MachineId && m.BranchId == branchId));
        if (filter.MachineId is int machineId)
            scoped = scoped.Where(a => a.MachineId == machineId);
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            scoped = scoped.Where(a => a.Message.Contains(q) || db.Machines.Any(m => m.Id == a.MachineId && m.Name.Contains(q)));
        }

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

        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var page = Math.Max(filter.Page, 1);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(a => a.Status == AlertStatus.Resolved).ThenByDescending(a => a.Level)
            .ThenByDescending(a => a.ResolvedAt ?? a.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new
            {
                a.Id,
                a.Level,
                a.Status,
                a.Message,
                a.Source,
                a.MachineId,
                Machine = db.Machines.Where(m => m.Id == a.MachineId).Select(m => new { m.Name, m.BranchId, Branch = m.Branch!.Name }).FirstOrDefault(),
                HandledBy = db.Users.Where(u => u.Id == a.HandledById).Select(u => u.Name).FirstOrDefault(),
                a.CreatedAt,
                a.ResolvedAt,
                a.Code,
            })
            .ToListAsync(ct);

        var items = rows.Select(a => new AlertListItem(
            a.Id, Text(a.Level), Text(a.Status), a.Message, a.Source, a.MachineId, a.Machine?.Name, a.Machine?.BranchId,
            a.Machine?.Branch, a.HandledBy, a.CreatedAt, a.ResolvedAt, a.Code is not null)).ToList();
        return new AlertList(new PagedResult<AlertListItem>(items, total, page, pageSize), counts);
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
                HandledBy = db.Users.Where(u => u.Id == a.HandledById).Select(u => u.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (alert is null)
            return null;

        var a = alert.Alert;
        var since = Now.AddDays(-30);
        var recurrences = a.Code is null || a.MachineId is null
            ? 0
            : await db.Alerts.CountAsync(x => x.MachineId == a.MachineId && x.Code == a.Code && x.Id != a.Id && x.CreatedAt >= since, ct);

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
            alert.Machine?.BranchId, alert.Machine?.Branch, a.RuleId, alert.Rule, alert.HandledBy, a.CreatedAt, a.ResolvedAt,
            recurrences, history.OrderBy(h => h.At).ToList());
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
                    r.Rule.CriticalThreshold, r.Rule.DurationMinutes, r.Rule.IsActive))
                .OrderBy(o => o.BranchName)
                .ToList();

            return new RuleItem(
                company?.Id, metric.Key, metric.Name, metric.Description, metric.Unit, metric.LessIsWorse, metric.HasThresholds,
                metric.HasDuration, metric.Available,
                company is null ? fallback.Warning : company.WarningThreshold,
                company is null ? fallback.Critical : company.CriticalThreshold,
                company?.DurationMinutes ?? fallback.DurationMinutes,
                company?.IsActive ?? fallback.Active,
                overrides);
        }).ToList();
    }
}

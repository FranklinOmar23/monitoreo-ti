using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>Escrituras de alertas (atender, resolver) y reglas (W-23). Todo queda en auditoría.</summary>
public sealed class AlertCommands(
    MonitoreoDbContext db,
    ICurrentUser user,
    IAuditTrail audit,
    IRealtimeNotifier notifier,
    TenantContext tenant,
    TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>La alerta, si el usuario puede verla (mismo criterio que la lista).</summary>
    private async Task<(Alert Alert, int? BranchId)?> FindAlertAsync(long id, CancellationToken ct)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
            return null;

        var branchId = alert.DeviceId is int deviceId
            ? await db.NetworkDevices.Where(d => d.Id == deviceId).Select(d => (int?)d.BranchId).FirstOrDefaultAsync(ct)
            : alert.MachineId is null
                ? null
                : await db.Machines.Where(m => m.Id == alert.MachineId).Select(m => (int?)m.BranchId).FirstOrDefaultAsync(ct);
        var allowed = await user.AllowedBranchIdsAsync(ct);
        if (allowed is not null && (branchId is null || !allowed.Contains(branchId.Value)))
            return null;
        return (alert, branchId);
    }

    /// <summary>Un técnico toma la alerta: pasa a "en proceso" a su nombre.</summary>
    public Task<CommandResult> AttendAsync(long id, AlertActionInput input, CancellationToken ct) =>
        ChangeAsync(id, input, AlertStatus.InProgress, "alerta.atender", ct);

    /// <summary>
    /// Resolver a mano. Si es una alerta del motor y el problema sigue, el motor abrirá una nueva en su próxima
    /// revisión: así no se pierde un problema real cerrado por error.
    /// </summary>
    public Task<CommandResult> ResolveAsync(long id, AlertActionInput input, CancellationToken ct) =>
        ChangeAsync(id, input, AlertStatus.Resolved, "alerta.resolver", ct);

    private async Task<CommandResult> ChangeAsync(long id, AlertActionInput input, AlertStatus status, string action, CancellationToken ct)
    {
        if (await FindAlertAsync(id, ct) is not { } found)
            return CommandResult.NotFound();
        var (alert, branchId) = found;
        if (alert.Status == AlertStatus.Resolved)
            return CommandResult.Conflict("La alerta ya está resuelta.");
        if (alert.Status == status)
            return CommandResult.Conflict("La alerta ya está en proceso.");

        var before = new { estado = PanelQueries.Text(alert.Status) };
        alert.Status = status;
        alert.HandledById = user.UserId;
        if (status == AlertStatus.Resolved)
            alert.ResolvedAt = Now;
        await db.SaveChangesAsync(ct);

        var note = string.IsNullOrWhiteSpace(input.Nota) ? null : input.Nota.Trim();
        await audit.RecordAsync(action, "alertas", id.ToString(), before, new { estado = PanelQueries.Text(status), nota = note }, ct);
        if (branchId is int branch && tenant.CompanyId is int company)
            await notifier.AlertChangedAsync(company, branch, id, alert.MachineId, isNew: false, ct);
        return CommandResult.Ok(id);
    }

    // ------------------------------------------------------------------ reglas

    /// <summary>
    /// Crea o cambia la regla de una métrica. La de toda la empresa solo la cambia quien ve todas las sucursales;
    /// la de una sucursal, quien tiene acceso a esa sucursal.
    /// </summary>
    public async Task<CommandResult> SaveRuleAsync(RuleInput input, CancellationToken ct)
    {
        var metric = RuleCatalog.Find(input.Metrica)!;
        var allowed = await user.AllowedBranchIdsAsync(ct);
        if (input.SucursalId is int branchId)
        {
            if ((allowed is not null && !allowed.Contains(branchId)) || !await db.Branches.AnyAsync(b => b.Id == branchId, ct))
                return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        }
        else if (allowed is not null)
        {
            return CommandResult.Invalid("Solo puede ajustar las reglas de sus sucursales.");
        }

        var key = EnumMemberConverter<AlertMetric>.FromProvider(metric.Key);
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Metric == key && r.BranchId == input.SucursalId, ct);
        var before = rule is null ? null : Snapshot(rule);
        if (rule is null)
        {
            rule = new AlertRule { Metric = key, BranchId = input.SucursalId };
            db.AlertRules.Add(rule);
        }

        // Las métricas sin umbrales (servicio detenido, antivirus…) conservan el valor de la sección 14: solo se activan o no.
        var fallback = RuleSet.Defaults.GetValueOrDefault(metric.Key);
        rule.Name = metric.Name;
        rule.Operator = metric.LessIsWorse ? ComparisonOperator.LessThan : ComparisonOperator.GreaterThan;
        rule.WarningThreshold = metric.HasThresholds ? input.UmbralAdvertencia : fallback?.Warning;
        rule.CriticalThreshold = metric.HasThresholds ? input.UmbralCritico : fallback?.Critical;
        rule.DurationMinutes = metric.HasDuration ? input.DuracionMin : 0;
        rule.IsActive = input.Activa;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(before is null ? "regla.crear" : "regla.editar", "reglas_alerta", rule.Id.ToString(), before, Snapshot(rule), ct);
        return CommandResult.Ok(rule.Id);
    }

    /// <summary>Quitar el ajuste de una sucursal (vuelve a la regla de la empresa) o el de la empresa (vuelve a la sección 14).</summary>
    public async Task<CommandResult> DeleteRuleAsync(int id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        var allowed = await user.AllowedBranchIdsAsync(ct);
        if (rule is null || (allowed is not null && (rule.BranchId is null || !allowed.Contains(rule.BranchId.Value))))
            return CommandResult.NotFound();

        var before = Snapshot(rule);
        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("regla.eliminar", "reglas_alerta", id.ToString(), before, null, ct);
        return CommandResult.Ok(id);
    }

    private static object Snapshot(AlertRule r) => new
    {
        metrica = EnumMemberConverter<AlertMetric>.ToProvider(r.Metric),
        sucursalId = r.BranchId,
        advertencia = r.WarningThreshold,
        critico = r.CriticalThreshold,
        duracionMin = r.DurationMinutes,
        activa = r.IsActive,
    };
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>Tareas programadas desde el panel (6.5): ver, crear, editar, pausar y eliminar.</summary>
public sealed class ScheduledTasks(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<ScheduledTaskItem>> ListAsync(CancellationToken ct)
    {
        var rows = await db.ScheduledTasks.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new
            {
                Task = t,
                Script = db.Scripts.Where(s => s.Id == t.ScriptId).Select(s => s.Name).FirstOrDefault(),
                Branch = db.Branches.Where(b => b.Id == t.BranchId).Select(b => b.Name).FirstOrDefault(),
                CreatedBy = db.Users.Where(u => u.Id == t.CreatedById).Select(u => u.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(r => ToItem(r.Task, r.Script ?? "", r.Branch, r.CreatedBy ?? "")).ToList();
    }

    public async Task<CommandResult> SaveAsync(int? id, ScheduledTaskInput input, CancellationToken ct)
    {
        ScheduledTask? row = null;
        if (id is int existing && (row = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == existing, ct)) is null)
            return CommandResult.NotFound();

        var name = input.Nombre.Trim();
        if (await db.ScheduledTasks.AnyAsync(t => t.Name == name && t.Id != (id ?? 0), ct))
            return CommandResult.Conflict("Ya existe una tarea con ese nombre.");
        if (!await db.Scripts.AnyAsync(s => s.Id == input.ScriptId, ct))
            return CommandResult.Invalid("El script no existe.");

        var allowed = await user.AllowedBranchIdsAsync(ct);
        // Quien ve solo algunas sucursales no programa "todos los equipos".
        if (input.Objetivo == "todos" && allowed is not null)
            return CommandResult.Invalid("Solo quien ve todas las sucursales puede programar en todos los equipos.");
        if (input.Objetivo == "sucursal" && (!await db.Branches.AnyAsync(b => b.Id == input.SucursalId, ct)
                || (allowed is not null && !allowed.Contains(input.SucursalId!.Value))))
            return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        var machineIds = input.Objetivo == "equipos" ? input.MaquinaIds!.Distinct().ToList() : [];
        if (machineIds.Count > 0)
        {
            var visible = await db.Machines.CountAsync(m => machineIds.Contains(m.Id) && m.Status != MachineStatus.Retired
                && (allowed == null || allowed.Contains(m.BranchId)), ct);
            if (visible != machineIds.Count)
                return CommandResult.Invalid("Algún equipo no existe o no tiene acceso a él.");
        }

        ScheduleRules.TryParseTime(input.Hora, out var time);
        var before = row is null ? null : Snapshot(row);
        row ??= db.ScheduledTasks.Add(new ScheduledTask { CreatedById = user.UserId }).Entity;
        if (id is not null)
            row.UpdatedAt = Now;
        row.Name = name;
        row.ScriptId = input.ScriptId;
        row.Target = input.Objetivo;
        row.BranchId = input.Objetivo == "sucursal" ? input.SucursalId : null;
        row.MachineIds = machineIds.Count > 0 ? string.Join(',', machineIds) : null;
        row.Frequency = input.Frecuencia;
        row.Time = time;
        row.Date = input.Frecuencia == "una_vez" ? input.Fecha : null;
        row.WeekDays = input.Frecuencia == "semanal" ? (byte?)input.DiasSemana : null;
        row.MonthDay = input.Frecuencia == "mensual" ? (byte?)input.DiaMes : null;
        row.TimeoutSeconds = input.TimeoutS ?? RemoteRules.DefaultTimeoutSeconds;
        row.IsActive = input.Activa;
        row.NextRunAt = input.Activa ? ScheduleRules.Next(Spec(row), Now) : null;
        if (input.Activa && row.NextRunAt is null)
            return CommandResult.Invalid("Esa fecha y hora ya pasaron.");
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(before is null ? "tarea.crear" : "tarea.editar", "tareas_programadas", row.Id.ToString(), before, Snapshot(row), ct);
        return CommandResult.Ok(row.Id);
    }

    public async Task<CommandResult> DeleteAsync(int id, CancellationToken ct)
    {
        var row = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row is null)
            return CommandResult.NotFound();
        db.ScheduledTasks.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("tarea.eliminar", "tareas_programadas", id.ToString(), Snapshot(row), null, ct);
        return CommandResult.Ok(id);
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    internal static ScheduleSpec Spec(ScheduledTask t) => new(t.Frequency, t.Time, t.Date, t.WeekDays, t.MonthDay);

    internal static IReadOnlyList<int> Ids(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).ToList();

    private static ScheduledTaskItem ToItem(ScheduledTask t, string script, string? branch, string createdBy) => new(
        t.Id, t.Name, t.ScriptId, script, t.Target, t.BranchId, branch, Ids(t.MachineIds), t.Frequency, t.Time.ToString("HH\\:mm"),
        t.Date, t.WeekDays, t.MonthDay, t.TimeoutSeconds, t.IsActive, t.NextRunAt, t.LastRunAt, t.LastBatchId, createdBy,
        ScheduleRules.Describe(Spec(t)));

    private static object Snapshot(ScheduledTask t) => new
    {
        nombre = t.Name,
        scriptId = t.ScriptId,
        objetivo = t.Target,
        sucursalId = t.BranchId,
        maquinas = t.MachineIds,
        frecuencia = t.Frequency,
        hora = t.Time.ToString("HH\\:mm"),
        fecha = t.Date,
        diasSemana = t.WeekDays,
        diaMes = t.MonthDay,
        activa = t.IsActive,
    };
}

/// <summary>
/// Ejecuta las tareas a las que les tocó (cada minuto, empresa por empresa): crea el lote de comandos firmados a nombre
/// de quien creó la tarea, como una ejecución manual, y calcula la siguiente. Un equipo apagado lo recibe al volver.
/// </summary>
public sealed class ScheduledTaskRunner(
    IServiceScopeFactory scopes, ICommandDispatcher dispatcher, TimeProvider clock, ILogger<ScheduledTaskRunner> logger)
{
    public async Task<int> RunDueAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
        var signer = scope.ServiceProvider.GetRequiredService<ICompanySigner>();
        var now = clock.GetUtcNow().UtcDateTime;

        var due = await db.ScheduledTasks.Where(t => t.IsActive && t.NextRunAt != null && t.NextRunAt <= now).ToListAsync(cancellationToken);
        if (due.Count == 0)
            return 0;

        // El plan puede haber cambiado desde que se programó: sin módulo remoto no se ejecuta nada.
        var today = DateOnly.FromDateTime(now);
        var included = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.StartDate <= today && (s.EndDate == null || s.EndDate >= today))
            .AnyAsync(s => db.PlanModules.Any(m => m.PlanId == s.PlanId && m.Module == ModuleKey.Remote), cancellationToken);

        var dispatched = new List<long>();
        foreach (var task in due)
        {
            string? skipped = null;
            var script = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == task.ScriptId, cancellationToken);
            if (!included)
                skipped = "el plan de la empresa no incluye el acceso remoto";
            else if (script is null || !await RemoteCommands.IsSignedAsync(signer, companyId, script, cancellationToken))
                skipped = "la firma del script no es válida";

            var machines = skipped is null ? await TargetsAsync(db, task, cancellationToken) : [];
            if (skipped is null && machines.Count > 0)
            {
                var language = Text(script!.Language);
                var (batch, rows) = await RemoteCommands.CreateBatchAsync(
                    db, script, language, script.Content, machines, task.CreatedById, task.TimeoutSeconds, now, cancellationToken);
                task.LastBatchId = batch;
                dispatched.AddRange(rows.Select(r => r.Id));
                db.AuditLogs.Add(new AuditLog
                {
                    Action = "tarea.ejecutar",
                    Entity = "tareas_programadas",
                    EntityId = task.Id.ToString(),
                    Details = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        despues = RemoteCommands.AuditDetail(batch, script, language, script.Content, machines, task.TimeoutSeconds),
                    }),
                });
            }
            else
            {
                logger.LogWarning("Tarea {TaskId} de la empresa {CompanyId}: no se ejecutó ({Reason}).",
                    task.Id, companyId, skipped ?? "no hay equipos con agente en su objetivo");
            }

            task.LastRunAt = now;
            task.NextRunAt = task.Frequency == "una_vez" ? null : ScheduleRules.Next(ScheduledTasks.Spec(task), now);
            // Una sola vez: después de ejecutarse queda en pausa (se conserva para el historial).
            if (task.NextRunAt is null)
                task.IsActive = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (dispatched.Count > 0)
            await dispatcher.DispatchAsync(companyId, dispatched, cancellationToken);
        return due.Count;
    }

    /// <summary>Equipos del objetivo que tienen agente aprobado y no están de baja.</summary>
    private static async Task<List<int>> TargetsAsync(MonitoreoDbContext db, ScheduledTask task, CancellationToken ct)
    {
        var query = db.Machines.AsNoTracking().Where(m => m.Status != MachineStatus.Retired
            && db.Agents.Any(a => a.MachineId == m.Id && !a.IsRevoked && a.IsApproved));
        if (task.Target == "sucursal")
            query = query.Where(m => m.BranchId == task.BranchId);
        else if (task.Target == "equipos")
        {
            var ids = ScheduledTasks.Ids(task.MachineIds);
            query = query.Where(m => ids.Contains(m.Id));
        }
        return await query.Select(m => m.Id).Take(RemoteRules.MaxMachinesPerRun * 5).ToListAsync(ct);
    }
}

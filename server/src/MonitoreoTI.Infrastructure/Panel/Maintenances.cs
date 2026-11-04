using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Calendario de mantenimiento (W-27): preventivos programados y correctivos realizados de los equipos que ve el
/// usuario. Cada cambio queda en auditoría (y en el historial del equipo).
/// </summary>
public sealed class Maintenances(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    private async Task<IQueryable<Maintenance>> VisibleAsync(CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var machines = db.Machines.Where(m => allowed == null || allowed.Contains(m.BranchId)).Select(m => m.Id);
        return db.Maintenances.Where(x => machines.Contains(x.MachineId));
    }

    public async Task<MaintenanceList> ListAsync(MaintenanceFilter filter, CancellationToken ct)
    {
        var visible = await VisibleAsync(ct);
        var scoped = visible;
        if (filter.BranchId is int branch)
            scoped = scoped.Where(x => db.Machines.Any(m => m.Id == x.MachineId && m.BranchId == branch));
        if (filter.MachineId is int machine)
            scoped = scoped.Where(x => x.MachineId == machine);

        var rows = await scoped.AsNoTracking()
            .Where(x => (x.ScheduledDate >= filter.From && x.ScheduledDate <= filter.To) || (x.CompletedDate >= filter.From && x.CompletedDate <= filter.To))
            .OrderBy(x => x.CompletedDate ?? x.ScheduledDate)
            .Select(x => new
            {
                x.Id,
                x.MachineId,
                x.Type,
                x.Description,
                x.ScheduledDate,
                x.CompletedDate,
                x.TechnicianId,
                Machine = db.Machines.Where(m => m.Id == x.MachineId).Select(m => new { m.Name, m.Type, m.BranchId, Branch = m.Branch!.Name }).First(),
                Technician = db.Users.Where(u => u.Id == x.TechnicianId).Select(u => u.Name).FirstOrDefault(),
            })
            .Take(1000)
            .ToListAsync(ct);

        var today = Today;
        var overdue = await visible.CountAsync(x => x.CompletedDate == null && x.ScheduledDate < today, ct);
        var items = rows.Select(x => new MaintenanceItem(
            x.Id, x.MachineId, x.Machine.Name, Text(x.Machine.Type), x.Machine.BranchId, x.Machine.Branch, Text(x.Type), x.Description,
            x.ScheduledDate, x.CompletedDate, x.TechnicianId, x.Technician, MaintenanceRules.Status(x.ScheduledDate, x.CompletedDate, today))).ToList();
        return new MaintenanceList(items, overdue);
    }

    public async Task<CommandResult> SaveAsync(int? id, MaintenanceInput input, CancellationToken ct)
    {
        Maintenance? row = null;
        if (id is int existing && (row = await (await VisibleAsync(ct)).FirstOrDefaultAsync(x => x.Id == existing, ct)) is null)
            return CommandResult.NotFound();

        var allowed = await user.AllowedBranchIdsAsync(ct);
        if (!await db.Machines.AnyAsync(m => m.Id == input.MaquinaId && m.Status != MachineStatus.Retired
                && (allowed == null || allowed.Contains(m.BranchId)), ct))
            return CommandResult.Invalid("El equipo no existe o no tiene acceso a él.");
        if (input.TecnicoId is int technician && !await db.UserCompanies.AnyAsync(u => u.UserId == technician && u.CompanyId == user.CompanyId && u.IsActive, ct))
            return CommandResult.Invalid("El técnico no pertenece a la empresa.");
        if (input.FechaRealizada is DateOnly done && done > Today)
            return CommandResult.Invalid("La fecha en que se hizo no puede ser futura.");

        var before = row is null ? null : Snapshot(row);
        row ??= db.Maintenances.Add(new Maintenance()).Entity;
        row.MachineId = input.MaquinaId;
        row.Type = input.Tipo == "correctivo" ? MaintenanceType.Corrective : MaintenanceType.Preventive;
        row.Description = string.IsNullOrWhiteSpace(input.Descripcion) ? null : input.Descripcion.Trim();
        row.ScheduledDate = input.FechaProgramada;
        row.CompletedDate = input.FechaRealizada;
        row.TechnicianId = input.TecnicoId;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(before is null ? "mantenimiento.crear" : "mantenimiento.editar", "maquinas", row.MachineId.ToString(), before, Snapshot(row), ct);
        return CommandResult.Ok(row.Id);
    }

    /// <summary>Marcar como hecho (hoy o la fecha indicada), con una nota opcional para el historial.</summary>
    public async Task<CommandResult> CompleteAsync(int id, MaintenanceDoneInput input, CancellationToken ct)
    {
        var row = await (await VisibleAsync(ct)).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return CommandResult.NotFound();
        if (row.CompletedDate is not null)
            return CommandResult.Conflict("El mantenimiento ya está marcado como hecho.");
        var date = input.Fecha ?? Today;
        if (date > Today)
            return CommandResult.Invalid("La fecha en que se hizo no puede ser futura.");

        var before = Snapshot(row);
        row.CompletedDate = date;
        row.TechnicianId ??= user.UserId;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("mantenimiento.realizar", "maquinas", row.MachineId.ToString(), before,
            new { tipo = Text(row.Type), realizado = row.CompletedDate, nota = string.IsNullOrWhiteSpace(input.Nota) ? null : input.Nota.Trim() }, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Solo se elimina lo que no se ha hecho (lo hecho es historial).</summary>
    public async Task<CommandResult> DeleteAsync(int id, CancellationToken ct)
    {
        var row = await (await VisibleAsync(ct)).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (row is null)
            return CommandResult.NotFound();
        if (row.CompletedDate is not null)
            return CommandResult.Conflict("Un mantenimiento hecho no se elimina: es parte del historial del equipo.");
        db.Maintenances.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("mantenimiento.eliminar", "maquinas", row.MachineId.ToString(), Snapshot(row), null, ct);
        return CommandResult.Ok(id);
    }

    private static object Snapshot(Maintenance m) => new
    {
        tipo = Text(m.Type),
        descripcion = m.Description,
        programado = m.ScheduledDate,
        realizado = m.CompletedDate,
        tecnicoId = m.TechnicianId,
    };
}

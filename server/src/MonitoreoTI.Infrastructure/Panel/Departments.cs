using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Settings;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Departamentos por sucursal (decisión 79). Se ven con equipos.ver (filtrados por las sucursales del usuario) y se
/// administran desde la sucursal con sucursales.editar; cada cambio queda en auditoría. No se elimina un departamento
/// con equipos (tampoco dados de baja: la ficha guarda a cuál pertenecían).
/// </summary>
public sealed class Departments(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit)
{
    private async Task<bool> CanUseBranchAsync(int branchId, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        return (allowed is null || allowed.Contains(branchId)) && await db.Branches.AnyAsync(b => b.Id == branchId, ct);
    }

    public async Task<IReadOnlyList<DepartmentItem>> ListAsync(int? branchId, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        return await db.Departments.AsNoTracking()
            .Where(d => (branchId == null || d.BranchId == branchId) && (allowed == null || allowed.Contains(d.BranchId)))
            .OrderBy(d => d.Name)
            .Select(d => new DepartmentItem(
                d.Id, d.Name,
                db.Machines.Count(m => m.DepartmentId == d.Id && m.Status != MachineStatus.Retired),
                d.BranchId,
                db.Branches.Where(b => b.Id == d.BranchId).Select(b => b.Name).FirstOrDefault() ?? "",
                d.ResponsibleUserId,
                db.Users.Where(u => u.Id == d.ResponsibleUserId).Select(u => u.Name).FirstOrDefault()))
            .ToListAsync(ct);
    }

    public async Task<CommandResult> SaveAsync(int branchId, int? id, DepartmentInput input, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(branchId, ct))
            return CommandResult.NotFound();
        var name = input.Nombre.Trim();
        if (await db.Departments.AnyAsync(d => d.BranchId == branchId && d.Id != id && d.Name == name, ct))
            return CommandResult.Conflict("Esa sucursal ya tiene un departamento con ese nombre.");
        if (input.ResponsableId is int responsible
            && !await db.UserCompanies.AnyAsync(m => m.UserId == responsible && m.CompanyId == user.CompanyId && m.IsActive, ct))
            return CommandResult.Invalid("El responsable debe ser un usuario activo de la empresa.");

        Department? department;
        object? before = null;
        if (id is null)
        {
            department = new Department { BranchId = branchId };
            db.Departments.Add(department);
        }
        else
        {
            department = await db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.BranchId == branchId, ct);
            if (department is null)
                return CommandResult.NotFound();
            before = Snapshot(department);
        }
        department.Name = name;
        department.ResponsibleUserId = input.ResponsableId;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(id is null ? "departamento.crear" : "departamento.editar", "departamentos", department.Id.ToString(),
            before, Snapshot(department), ct);
        return CommandResult.Ok(department.Id);
    }

    public async Task<CommandResult> DeleteAsync(int branchId, int id, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(branchId, ct))
            return CommandResult.NotFound();
        var department = await db.Departments.FirstOrDefaultAsync(d => d.Id == id && d.BranchId == branchId, ct);
        if (department is null)
            return CommandResult.NotFound();
        var machines = await db.Machines.CountAsync(m => m.DepartmentId == id, ct);
        if (machines > 0)
            return CommandResult.Conflict($"El departamento tiene {machines} {(machines == 1 ? "equipo" : "equipos")}. Páselos a otro antes de eliminarlo.");
        db.Departments.Remove(department);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("departamento.eliminar", "departamentos", id.ToString(), Snapshot(department), null, ct);
        return CommandResult.Ok(id);
    }

    private static object Snapshot(Department d) => new { nombre = d.Name, sucursalId = d.BranchId, responsableId = d.ResponsibleUserId };
}

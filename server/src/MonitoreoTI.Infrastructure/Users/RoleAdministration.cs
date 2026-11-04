using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Users;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Users;

/// <summary>
/// Roles y permisos (W-38). Los roles del sistema (sin empresa) se ven y se duplican pero no se cambian: así el
/// Administrador siempre conserva todos los permisos. Los roles propios de la empresa se crean, editan y borran
/// (solo si nadie los usa). Un cambio de permisos llega a los usuarios en su próxima renovación (15 minutos).
/// </summary>
public sealed class RoleAdministration(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit)
{
    public async Task<IReadOnlyList<RoleItem>> ListAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var roles = await db.Roles.AsNoTracking()
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Description,
                r.CompanyId,
                Permissions = r.Permissions.Select(p => p.Permission!.Key).ToList(),
                Users = db.UserCompanies.Count(m => m.RoleId == r.Id && m.CompanyId == companyId && m.IsActive),
            })
            .ToListAsync(ct);

        return roles
            .OrderBy(r => r.CompanyId is not null).ThenBy(r => r.Id)
            .Select(r => new RoleItem(r.Id, r.Name, r.Description, r.CompanyId is null, r.Permissions.Order().ToList(), r.Users))
            .ToList();
    }

    public async Task<IReadOnlyList<PermissionItem>> PermissionsAsync(CancellationToken ct)
    {
        var rows = await db.Permissions.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
        return rows
            .Select(p => new PermissionItem(p.Key, p.Description ?? p.Key, PermissionGroups.Of(p.Key), PermissionKeys.RequiringTwoFactor.Contains(p.Key)))
            .OrderBy(p => PermissionGroups.Order.ToList().IndexOf(p.Group))
            .ToList();
    }

    public async Task<CommandResult> CreateAsync(RoleInput input, CancellationToken ct)
    {
        var name = input.Nombre.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == name, ct))
            return CommandResult.Conflict($"Ya existe un rol llamado \"{name}\".");
        var permissions = await PermissionIdsAsync(input.Permisos, ct);
        if (permissions is null)
            return CommandResult.Invalid("Algún permiso no existe.");

        var role = new Role { Name = name, Description = Clean(input.Descripcion) };
        foreach (var id in permissions)
            role.Permissions.Add(new RolePermission { PermissionId = id });
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("rol.crear", "roles", role.Id.ToString(), null, Snapshot(name, role.Description, input.Permisos), ct);
        return CommandResult.Ok(role.Id);
    }

    public async Task<CommandResult> UpdateAsync(int id, RoleInput input, CancellationToken ct)
    {
        var role = await db.Roles.Include(r => r.Permissions).ThenInclude(p => p.Permission).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
            return CommandResult.NotFound();
        if (role.CompanyId is null)
            return CommandResult.Invalid("Los roles del sistema no se cambian. Duplíquelo para crear uno a su medida.");

        var name = input.Nombre.Trim();
        if (await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct))
            return CommandResult.Conflict($"Ya existe un rol llamado \"{name}\".");
        var permissions = await PermissionIdsAsync(input.Permisos, ct);
        if (permissions is null)
            return CommandResult.Invalid("Algún permiso no existe.");

        var before = Snapshot(role.Name, role.Description, role.Permissions.Select(p => p.Permission!.Key));
        role.Name = name;
        role.Description = Clean(input.Descripcion);
        foreach (var removed in role.Permissions.Where(p => !permissions.Contains(p.PermissionId)).ToList())
            role.Permissions.Remove(removed);
        foreach (var added in permissions.Where(p => role.Permissions.All(rp => rp.PermissionId != p)))
            role.Permissions.Add(new RolePermission { PermissionId = added });
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("rol.editar", "roles", id.ToString(), before, Snapshot(name, role.Description, input.Permisos), ct);
        return CommandResult.Ok(id);
    }

    public async Task<CommandResult> DeleteAsync(int id, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
            return CommandResult.NotFound();
        if (role.CompanyId is null)
            return CommandResult.Invalid("Los roles del sistema no se eliminan.");

        // También cuentan los usuarios desactivados: al reactivarlos necesitan su rol.
        var companyId = user.CompanyId;
        var users = await db.UserCompanies.CountAsync(m => m.RoleId == id && m.CompanyId == companyId, ct);
        if (users > 0)
            return CommandResult.Conflict(users == 1 ? "Un usuario tiene este rol: cámbielo antes de eliminarlo." : $"{users} usuarios tienen este rol: cámbielos antes de eliminarlo.");

        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("rol.eliminar", "roles", id.ToString(), new { nombre = role.Name }, null, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Ids de las claves pedidas; null si alguna no existe.</summary>
    private async Task<HashSet<int>?> PermissionIdsAsync(IReadOnlyList<string> keys, CancellationToken ct)
    {
        var wanted = keys.Distinct().ToList();
        var found = await db.Permissions.Where(p => wanted.Contains(p.Key)).Select(p => p.Id).ToListAsync(ct);
        return found.Count == wanted.Count ? found.ToHashSet() : null;
    }

    private static object Snapshot(string name, string? description, IEnumerable<string> permissions) =>
        new { nombre = name, descripcion = description, permisos = permissions.Distinct().Order().ToList() };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

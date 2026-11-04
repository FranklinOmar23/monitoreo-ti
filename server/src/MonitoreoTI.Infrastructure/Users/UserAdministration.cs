using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Users;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Users;

/// <summary>
/// Usuarios de la empresa (W-36, W-37). Las contraseñas no las escribe el administrador: se entrega un enlace de un
/// solo uso. Los usuarios no se borran: se desactiva su pertenencia a la empresa. Un usuario que también está en
/// otra empresa es una cuenta compartida: aquí solo se cambian su rol, sus sucursales y si está activo.
/// </summary>
public sealed class UserAdministration(
    MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit, IEmailSender mailer, AuthOptions options, TimeProvider clock)
{
    /// <summary>Rol del sistema que ve todo; siempre debe quedar al menos uno activo.</summary>
    public const string AdministratorRole = "Administrador";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<UserItem>> ListAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var rows = await db.UserCompanies.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new
            {
                u.Id,
                u.Name,
                u.Email,
                m.RoleId,
                Active = m.IsActive && u.IsActive,
                HasTwoFactor = u.TotpSecret != null,
                u.LastLoginAt,
                u.CreatedAt,
                u.LockedUntil,
                Shared = u.IsSuperAdmin || db.UserCompanies.Any(o => o.UserId == u.Id && o.CompanyId != companyId),
            })
            .ToListAsync(ct);

        var roles = await RolePermissionsAsync(ct);
        var branches = (await db.UserBranches.AsNoTracking()
                .Select(b => new { b.UserId, b.BranchId, b.Branch!.Name })
                .ToListAsync(ct))
            .ToLookup(b => b.UserId, b => new BranchRef(b.BranchId, b.Name));
        var now = Now;

        return rows
            .Select(r =>
            {
                var role = roles.GetValueOrDefault(r.RoleId);
                return new UserItem(
                    r.Id, r.Name, r.Email, r.RoleId, role?.Name ?? "", branches[r.Id].OrderBy(b => b.Name).ToList(),
                    r.Active, r.HasTwoFactor,
                    role?.Permissions.Any(PermissionKeys.RequiringTwoFactor.Contains) ?? false,
                    r.LastLoginAt, r.CreatedAt, r.LastLoginAt is null, r.LockedUntil > now, r.Shared, r.Id == user.UserId);
            })
            .OrderBy(u => !u.IsActive).ThenBy(u => u.Name)
            .ToList();
    }

    public async Task<(CommandResult Result, PasswordLink? Link)> CreateAsync(UserInput input, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var email = input.Correo.Trim();
        var invalid = await ValidateAssignmentAsync(input, ct);
        if (invalid is not null)
            return (invalid, null);

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (existing is { IsSuperAdmin: true })
            return (CommandResult.Conflict("Ese correo es de una cuenta del proveedor del sistema."), null);
        if (existing is not null && await db.UserCompanies.AnyAsync(m => m.UserId == existing.Id && m.CompanyId == companyId, ct))
            return (CommandResult.Conflict("Ya hay un usuario con ese correo en la empresa."), null);
        if (await PlanLimitMessageAsync(ct) is { } limit)
            return (CommandResult.Invalid(limit), null);

        // Un correo que ya existe en otra empresa es la misma persona: se suma a esta empresa con su contraseña de siempre.
        var target = existing ?? new User
        {
            Name = input.Nombre.Trim(),
            Email = email,
            // Inservible hasta que el usuario cree la suya con el enlace.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), 11),
        };
        if (existing is null)
            db.Users.Add(target);
        target.Companies.Add(new UserCompany { CompanyId = companyId, RoleId = input.RolId });
        await db.SaveChangesAsync(ct);

        await SetBranchesAsync(target.Id, input.SucursalIds, ct);
        await audit.RecordAsync(existing is null ? "usuario.crear" : "usuario.agregar", "usuarios", target.Id.ToString(),
            null, await SnapshotAsync(target.Id, ct), ct);

        var link = existing is null ? await IssueLinkAsync(target, invitation: true, ct) : null;
        return (CommandResult.Ok(target.Id), link);
    }

    public async Task<CommandResult> UpdateAsync(int id, UserInput input, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(id, ct);
        if (membership is null)
            return CommandResult.NotFound();
        var invalid = await ValidateAssignmentAsync(input, ct);
        if (invalid is not null)
            return invalid;

        var target = await db.Users.FirstAsync(u => u.Id == id, ct);
        var name = input.Nombre.Trim();
        var email = input.Correo.Trim();
        var identityChanged = name != target.Name || !string.Equals(email, target.Email, StringComparison.OrdinalIgnoreCase);
        if (identityChanged && await IsSharedAsync(target, ct))
            return CommandResult.Invalid("Este usuario también pertenece a otra empresa: su nombre y su correo solo los cambia él.");
        if (!string.Equals(email, target.Email, StringComparison.OrdinalIgnoreCase) && await db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct))
            return CommandResult.Conflict("Ya existe otro usuario con ese correo.");

        if (input.RolId != membership.RoleId)
        {
            if (id == user.UserId)
                return CommandResult.Invalid("No puede cambiar su propio rol.");
            if (await IsLastAdministratorAsync(membership, ct))
                return CommandResult.Invalid("Debe quedar al menos un Administrador activo en la empresa.");
        }

        var before = await SnapshotAsync(id, ct);
        target.Name = name;
        target.Email = email;
        membership.RoleId = input.RolId;
        await db.SaveChangesAsync(ct);
        await SetBranchesAsync(id, input.SucursalIds, ct);

        await audit.RecordAsync("usuario.editar", "usuarios", id.ToString(), before, await SnapshotAsync(id, ct), ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Desactivar cierra sus sesiones en esta empresa; sus otras empresas no cambian.</summary>
    public async Task<CommandResult> SetActiveAsync(int id, bool active, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(id, ct);
        if (membership is null)
            return CommandResult.NotFound();
        if (membership.IsActive == active)
            return CommandResult.Ok(id);

        if (!active)
        {
            if (id == user.UserId)
                return CommandResult.Invalid("No puede desactivar su propio usuario.");
            if (await IsLastAdministratorAsync(membership, ct))
                return CommandResult.Invalid("Debe quedar al menos un Administrador activo en la empresa.");
        }
        else if (await PlanLimitMessageAsync(ct) is { } limit)
        {
            return CommandResult.Invalid(limit);
        }

        membership.IsActive = active;
        await db.SaveChangesAsync(ct);
        if (!active)
        {
            var companyId = user.CompanyId;
            var now = Now;
            await db.RefreshTokens.Where(t => t.UserId == id && t.CompanyId == companyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        }

        await audit.RecordAsync(active ? "usuario.activar" : "usuario.desactivar", "usuarios", id.ToString(), null, null, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Enlace para crear (si nunca entró) o restablecer la contraseña. También quita el bloqueo por intentos.</summary>
    public async Task<(CommandResult Result, PasswordLink? Link)> PasswordLinkAsync(int id, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(id, ct);
        if (membership is null)
            return (CommandResult.NotFound(), null);
        var target = await db.Users.FirstAsync(u => u.Id == id, ct);
        if (!membership.IsActive || !target.IsActive)
            return (CommandResult.Invalid("Active el usuario antes de enviarle un enlace."), null);
        if (await IsSharedAsync(target, ct))
            return (CommandResult.Invalid("Este usuario también pertenece a otra empresa: su contraseña solo la cambia él (\"¿Olvidó su contraseña?\")."), null);

        target.LockedUntil = null;
        target.FailedLoginAttempts = 0;
        await db.SaveChangesAsync(ct);
        return (CommandResult.Ok(id), await IssueLinkAsync(target, invitation: target.LastLoginAt is null, ct));
    }

    /// <summary>Teléfono perdido o cambiado: borra el 2FA y cierra sus sesiones; al entrar vuelve a ver el QR.</summary>
    public async Task<CommandResult> ResetTwoFactorAsync(int id, CancellationToken ct)
    {
        var membership = await FindMembershipAsync(id, ct);
        if (membership is null)
            return CommandResult.NotFound();
        var target = await db.Users.FirstAsync(u => u.Id == id, ct);
        if (await IsSharedAsync(target, ct))
            return CommandResult.Invalid("Este usuario también pertenece a otra empresa: su 2FA no se puede reiniciar desde aquí.");
        if (target.TotpSecret is null)
            return CommandResult.Invalid("Este usuario no tiene la verificación en dos pasos activada.");

        target.TotpSecret = null;
        target.LastTwoFactorAt = null;
        await db.SaveChangesAsync(ct);
        var now = Now;
        await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await audit.RecordAsync("usuario.reiniciar_2fa", "usuarios", id.ToString(), null, null, ct);
        return CommandResult.Ok(id);
    }

    // ------------------------------------------------------------------ apoyo

    /// <summary>usuario_empresa no tiene filtro por empresa: se busca siempre con la empresa de la sesión.</summary>
    private Task<UserCompany?> FindMembershipAsync(int userId, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        return db.UserCompanies.FirstOrDefaultAsync(m => m.UserId == userId && m.CompanyId == companyId, ct);
    }

    private async Task<bool> IsSharedAsync(User target, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        return target.IsSuperAdmin || await db.UserCompanies.AnyAsync(m => m.UserId == target.Id && m.CompanyId != companyId, ct);
    }

    private Task<int> AdministratorRoleIdAsync(CancellationToken ct) =>
        db.Roles.Where(r => r.CompanyId == null && r.Name == AdministratorRole).Select(r => r.Id).FirstAsync(ct);

    /// <summary>Es Administrador activo y no hay otro: quitarle el rol o desactivarlo dejaría la empresa sin quien la administre.</summary>
    private async Task<bool> IsLastAdministratorAsync(UserCompany membership, CancellationToken ct)
    {
        var adminRole = await AdministratorRoleIdAsync(ct);
        if (membership.RoleId != adminRole || !membership.IsActive)
            return false;
        var companyId = user.CompanyId;
        return !await db.UserCompanies.AnyAsync(m => m.CompanyId == companyId && m.UserId != membership.UserId && m.IsActive
            && m.RoleId == adminRole && db.Users.Any(u => u.Id == m.UserId && u.IsActive), ct);
    }

    /// <summary>El rol debe ser del sistema o de la empresa; las sucursales, de la empresa y del alcance de quien asigna.</summary>
    private async Task<CommandResult?> ValidateAssignmentAsync(UserInput input, CancellationToken ct)
    {
        if (!await db.Roles.AnyAsync(r => r.Id == input.RolId, ct))
            return CommandResult.Invalid("El rol no existe.");

        var ids = (input.SucursalIds ?? []).Distinct().ToList();
        if (ids.Count > 0 && await db.Branches.CountAsync(b => ids.Contains(b.Id), ct) != ids.Count)
            return CommandResult.Invalid("Alguna sucursal no existe.");

        // Quien ve solo algunas sucursales no da acceso a otras (ni a todas).
        var allowed = await user.AllowedBranchIdsAsync(ct);
        if (allowed is not null && (ids.Count == 0 || ids.Any(b => !allowed.Contains(b))))
            return CommandResult.Invalid("Solo puede asignar sucursales que usted ve.");
        return null;
    }

    private async Task SetBranchesAsync(int userId, IReadOnlyList<int>? branchIds, CancellationToken ct)
    {
        var wanted = (branchIds ?? []).Distinct().ToHashSet();
        var current = await db.UserBranches.Where(b => b.UserId == userId).ToListAsync(ct);
        db.UserBranches.RemoveRange(current.Where(b => !wanted.Contains(b.BranchId)));
        foreach (var id in wanted.Where(id => current.All(b => b.BranchId != id)))
            db.UserBranches.Add(new UserBranch { UserId = userId, BranchId = id });
        await db.SaveChangesAsync(ct);
    }

    private async Task<string?> PlanLimitMessageAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var max = await db.Companies.Where(c => c.Id == companyId).Select(c => c.Plan!.MaxUsers).FirstOrDefaultAsync(ct);
        if (max is not int limit)
            return null;
        var active = await db.UserCompanies.CountAsync(m => m.CompanyId == companyId && m.IsActive
            && db.Users.Any(u => u.Id == m.UserId && u.IsActive), ct);
        return active >= limit ? $"El plan de la empresa permite {limit} usuarios activos. Desactive alguno o cambie de plan." : null;
    }

    private sealed record RoleInfo(string Name, IReadOnlyList<string> Permissions);

    private async Task<Dictionary<int, RoleInfo>> RolePermissionsAsync(CancellationToken ct) =>
        (await db.Roles.AsNoTracking()
            .Select(r => new { r.Id, r.Name, Permissions = r.Permissions.Select(p => p.Permission!.Key).ToList() })
            .ToListAsync(ct))
        .ToDictionary(r => r.Id, r => new RoleInfo(r.Name, r.Permissions));

    private async Task<object> SnapshotAsync(int userId, CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var row = await db.UserCompanies.AsNoTracking()
            .Where(m => m.UserId == userId && m.CompanyId == companyId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Name, u.Email, Role = m.Role!.Name, m.IsActive })
            .FirstAsync(ct);
        var branches = await db.UserBranches.AsNoTracking().Where(b => b.UserId == userId)
            .OrderBy(b => b.Branch!.Name).Select(b => b.Branch!.Name).ToListAsync(ct);
        return new { nombre = row.Name, correo = row.Email, rol = row.Role, sucursales = branches, activo = row.IsActive };
    }

    /// <summary>
    /// Un enlace nuevo anula los anteriores sin usar. El token solo viaja en el correo (o al panel si el servidor no
    /// envía correos); en la base queda su hash y en la auditoría nada de él.
    /// </summary>
    private async Task<PasswordLink> IssueLinkAsync(User target, bool invitation, CancellationToken ct)
    {
        var now = Now;
        await db.PasswordResetTokens.Where(t => t.UserId == target.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);

        var (token, hash) = AuthTokenService.NewOpaqueToken();
        var expires = now + options.PasswordInvitationLifetime;
        db.PasswordResetTokens.Add(new PasswordResetToken { UserId = target.Id, TokenHash = hash, ExpiresAt = expires });
        await db.SaveChangesAsync(ct);

        var companyId = user.CompanyId;
        var company = await db.Companies.Where(c => c.Id == companyId).Select(c => c.Name).FirstAsync(ct);
        var link = options.PasswordResetUrl + token;
        var hours = options.PasswordInvitationLifetime.TotalHours;
        if (invitation)
        {
            await mailer.SendAsync(target.Email, $"Su usuario de MonitoreoTI en {company}",
                $"Hola, {target.Name}.\n\nSe creó su usuario de MonitoreoTI en {company}. Para crear su contraseña, abra este enlace " +
                $"(vence en {hours:0} horas) o péguelo en \"¿Olvidó su contraseña?\" del panel:\n{link}\n\n" +
                $"Después inicie sesión con su correo: {target.Email}", ct);
        }
        else
        {
            await mailer.SendAsync(target.Email, "Restablecer contraseña de MonitoreoTI",
                $"Hola, {target.Name}.\n\nUn administrador de {company} pidió restablecer su contraseña. Para crear una nueva, abra " +
                $"este enlace (vence en {hours:0} horas) o péguelo en \"¿Olvidó su contraseña?\" del panel:\n{link}\n\n" +
                "Al cambiarla se cierran sus sesiones abiertas.", ct);
        }

        await audit.RecordAsync(invitation ? "usuario.invitacion" : "usuario.enlace_contrasena", "usuarios", target.Id.ToString(),
            null, new { enviadoPorCorreo = mailer.Delivers, vence = expires }, ct);
        return new PasswordLink(target.Id, mailer.Delivers, mailer.Delivers ? null : link, expires);
    }
}

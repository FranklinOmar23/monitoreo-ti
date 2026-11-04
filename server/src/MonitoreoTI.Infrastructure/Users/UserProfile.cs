using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Users;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Users;

/// <summary>
/// Mi perfil (W-41): nombre, contraseña y sesiones abiertas del propio usuario. Las sesiones son sus refresh tokens
/// vigentes; cerrar una corta su renovación (el acceso ya emitido vence solo en 15 minutos como mucho).
/// </summary>
public sealed class UserProfile(MonitoreoDbContext db, IAuditTrail audit, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public Task<ProfileData?> GetAsync(int userId, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId && u.IsActive)
            .Select(u => new ProfileData(u.Id, u.Name, u.Email, u.TotpSecret != null, u.LastTwoFactorAt, u.NotifyByEmail, u.CreatedAt))
            .FirstOrDefaultAsync(ct);

    public async Task<CommandResult> RenameAsync(int userId, ProfileInput input, CancellationToken ct)
    {
        var me = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (me is null)
            return CommandResult.NotFound();

        var before = me.Name;
        me.Name = input.Nombre.Trim();
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("perfil.editar", "usuarios", userId.ToString(), new { nombre = before }, new { nombre = me.Name }, ct);
        return CommandResult.Ok(userId);
    }

    /// <summary>Pide la contraseña actual y cierra las demás sesiones (la de esta ventana sigue abierta).</summary>
    public async Task<CommandResult> ChangePasswordAsync(int userId, long? currentSession, ChangePasswordInput input, CancellationToken ct)
    {
        var me = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
        if (me is null)
            return CommandResult.NotFound();
        if (!BCrypt.Net.BCrypt.Verify(input.ContrasenaActual, me.PasswordHash))
            return CommandResult.Invalid("La contraseña actual no es correcta.");

        me.PasswordHash = BCrypt.Net.BCrypt.HashPassword(input.NuevaContrasena, 11);
        await db.SaveChangesAsync(ct);
        var closed = await RevokeAsync(userId, t => t.Id != currentSession, ct);

        await audit.RecordAsync("contrasena.cambiada", "usuarios", userId.ToString(), null, new { sesionesCerradas = closed }, ct);
        return CommandResult.Ok(userId);
    }

    public async Task<IReadOnlyList<OpenSessionItem>> SessionsAsync(int userId, long? currentSession, CancellationToken ct)
    {
        var now = Now;
        var rows = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.CreatedAt, t.ExpiresAt, t.IpAddress, t.Device, t.ReplacedById })
            .ToListAsync(ct);

        // Cada renovación crea un token nuevo: la sesión "empezó" cuando se inició el primero de la cadena.
        var started = await StartTimesAsync(userId, rows.Select(r => r.Id).ToList(), ct);
        return rows
            .Select(r => new OpenSessionItem(r.Id, started.GetValueOrDefault(r.Id, r.CreatedAt), r.ExpiresAt, r.IpAddress, r.Device, r.Id == currentSession))
            .OrderByDescending(s => s.Current).ThenByDescending(s => s.StartedAt)
            .ToList();
    }

    public async Task<CommandResult> CloseSessionAsync(int userId, long sessionId, CancellationToken ct)
    {
        var closed = await RevokeAsync(userId, t => t.Id == sessionId, ct);
        if (closed == 0)
            return CommandResult.NotFound();
        await audit.RecordAsync("sesion.cerrar", "usuarios", userId.ToString(), null, new { sesion = sessionId }, ct);
        return CommandResult.Ok(sessionId);
    }

    public async Task<int> CloseOtherSessionsAsync(int userId, long? currentSession, CancellationToken ct)
    {
        var closed = await RevokeAsync(userId, t => t.Id != currentSession, ct);
        if (closed > 0)
            await audit.RecordAsync("sesion.cerrar_otras", "usuarios", userId.ToString(), null, new { sesionesCerradas = closed }, ct);
        return closed;
    }

    private Task<int> RevokeAsync(int userId, System.Linq.Expressions.Expression<Func<Domain.Entities.Security.RefreshToken, bool>> which, CancellationToken ct)
    {
        var now = Now;
        return db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now).Where(which)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    /// <summary>Inicio de cada sesión: se sigue la cadena de renovaciones hacia atrás (reemplazado_por).</summary>
    private async Task<Dictionary<long, DateTime>> StartTimesAsync(int userId, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var since = Now.AddDays(-30);
        var chain = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.CreatedAt >= since)
            .Select(t => new { t.Id, t.CreatedAt, t.ReplacedById })
            .ToListAsync(ct);
        var previous = chain.Where(t => t.ReplacedById is not null).ToDictionary(t => t.ReplacedById!.Value, t => t.Id);
        var created = chain.ToDictionary(t => t.Id, t => t.CreatedAt);

        var result = new Dictionary<long, DateTime>();
        foreach (var id in ids)
        {
            var current = id;
            for (var guard = 0; guard < 10_000 && previous.TryGetValue(current, out var before); guard++)
                current = before;
            if (created.TryGetValue(current, out var at))
                result[id] = at;
        }
        return result;
    }
}

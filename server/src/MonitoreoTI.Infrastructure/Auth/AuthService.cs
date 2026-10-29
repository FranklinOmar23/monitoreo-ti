using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>
/// Inicio de sesión en pasos: contraseña → 2FA (o su alta) → empresa → sesión.
/// Las tablas de usuarios, membresías y refresh tokens no tienen empresa (se leen antes de conocerla);
/// todo lo que pertenece a una empresa se lee en un ámbito propio con el contexto de esa empresa.
/// </summary>
public sealed class AuthService(
    MonitoreoDbContext db,
    IServiceScopeFactory scopes,
    AuthTokenService tokens,
    TwoFactor twoFactor,
    SecretProtector protector,
    IEmailSender mailer,
    AuthOptions options,
    TimeProvider clock) : IAuthService
{
    // Hash BCrypt válido de una contraseña aleatoria: comparar contra él iguala el tiempo de respuesta
    // cuando el correo no existe, para no revelar qué correos están registrados.
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString(), 11);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed record Membership(int CompanyId, string CompanyName, string Role, IReadOnlyList<string> Permissions);

    public async Task<AuthOutcome<LoginResult>> LoginAsync(string email, string password, string? ip, CancellationToken cancellationToken)
    {
        var normalized = email.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);

        if (user is null)
        {
            BCrypt.Net.BCrypt.Verify(password, DummyHash);
            return AuthOutcome<LoginResult>.Fail(AuthError.InvalidCredentials);
        }

        if (user.LockedUntil > Now)
            return AuthOutcome<LoginResult>.Fail(AuthError.LockedOut);

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return AuthOutcome<LoginResult>.Fail(await RegisterFailureAsync(user, "login.fallido", ip, cancellationToken));

        if (!user.IsActive)
            return AuthOutcome<LoginResult>.Fail(AuthError.InvalidCredentials);

        user.FailedLoginAttempts = 0;
        await db.SaveChangesAsync(cancellationToken);

        var memberships = await LoadMembershipsAsync(user.Id, cancellationToken);

        if (user.TotpSecret is not null)
            return Ok(new LoginResult(LoginStep.TwoFactorRequired, tokens.CreateChallenge(user.Id, ChallengePurpose.TwoFactor)));

        if (RequiresTwoFactor(user, memberships))
        {
            var secret = TwoFactor.NewSecret();
            var uri = twoFactor.OtpAuthUri(secret, user.Email);
            return Ok(new LoginResult(
                LoginStep.TwoFactorSetupRequired,
                tokens.CreateChallenge(user.Id, ChallengePurpose.TwoFactorSetup, protector.Protect(secret)),
                TwoFactorSetup: new TwoFactorSetup(secret, uri, TwoFactor.QrPngDataUri(uri))));
        }

        return await ContinueAfterTwoFactorAsync(user, memberships, ip, cancellationToken);
    }

    public async Task<AuthOutcome<LoginResult>> VerifyTwoFactorAsync(string challenge, string code, string? ip, CancellationToken cancellationToken)
    {
        var validated = await tokens.ValidateChallengeAsync(challenge, ChallengePurpose.TwoFactor);
        var user = validated is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == validated.Value.UserId, cancellationToken);
        if (user is null || !user.IsActive || user.TotpSecret is null)
            return AuthOutcome<LoginResult>.Fail(AuthError.InvalidChallenge);

        if (user.LockedUntil > Now)
            return AuthOutcome<LoginResult>.Fail(AuthError.LockedOut);

        var secret = protector.Unprotect(user.TotpSecret);
        if (secret is null || !twoFactor.Verify(user.Id, secret, code))
        {
            var error = await RegisterFailureAsync(user, "2fa.fallido", ip, cancellationToken);
            return AuthOutcome<LoginResult>.Fail(error == AuthError.LockedOut ? error : AuthError.InvalidCode);
        }

        user.FailedLoginAttempts = 0;
        await db.SaveChangesAsync(cancellationToken);
        return await ContinueAfterTwoFactorAsync(user, await LoadMembershipsAsync(user.Id, cancellationToken), ip, cancellationToken);
    }

    public async Task<AuthOutcome<LoginResult>> ConfirmTwoFactorSetupAsync(string challenge, string code, string? ip, CancellationToken cancellationToken)
    {
        var validated = await tokens.ValidateChallengeAsync(challenge, ChallengePurpose.TwoFactorSetup);
        var secret = validated?.PendingSecret is { } pending ? protector.Unprotect(pending) : null;
        var user = validated is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == validated.Value.UserId, cancellationToken);

        // Si ya tiene 2FA (otra pestaña lo activó), este desafío no puede reemplazarlo.
        if (user is null || !user.IsActive || secret is null || user.TotpSecret is not null)
            return AuthOutcome<LoginResult>.Fail(AuthError.InvalidChallenge);

        if (!twoFactor.Verify(user.Id, secret, code))
            return AuthOutcome<LoginResult>.Fail(AuthError.InvalidCode);

        user.TotpSecret = protector.Protect(secret);
        await db.SaveChangesAsync(cancellationToken);

        var memberships = await LoadMembershipsAsync(user.Id, cancellationToken);
        foreach (var membership in memberships.DefaultIfEmpty())
            await AuditAsync(membership?.CompanyId, user.Id, "2fa.activado", null, ip, cancellationToken);

        return await ContinueAfterTwoFactorAsync(user, memberships, ip, cancellationToken);
    }

    public async Task<AuthOutcome<LoginResult>> SelectCompanyAsync(string challenge, int companyId, string? ip, CancellationToken cancellationToken)
    {
        var validated = await tokens.ValidateChallengeAsync(challenge, ChallengePurpose.Company);
        var user = validated is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == validated.Value.UserId, cancellationToken);
        if (user is null || !user.IsActive)
            return AuthOutcome<LoginResult>.Fail(AuthError.InvalidChallenge);

        var membership = (await LoadMembershipsAsync(user.Id, cancellationToken)).FirstOrDefault(m => m.CompanyId == companyId);
        if (membership is null)
            return AuthOutcome<LoginResult>.Fail(AuthError.CompanyNotAllowed);

        return Ok(new LoginResult(LoginStep.Completed, Session: await IssueSessionAsync(user, membership, ip, cancellationToken)));
    }

    public async Task<AuthOutcome<SessionTokens>> RefreshAsync(string refreshToken, string? ip, CancellationToken cancellationToken)
    {
        var hash = AuthTokenService.HashOpaqueToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (stored is null)
            return AuthOutcome<SessionTokens>.Fail(AuthError.InvalidRefreshToken);

        if (stored.RevokedAt is not null)
        {
            // Un refresh ya usado vuelve a aparecer: alguien lo copió. Se cierran todas las sesiones del usuario.
            await RevokeAllAsync(stored.UserId, cancellationToken);
            await AuditAsync(stored.CompanyId, stored.UserId, "sesion.reuso_refresh", null, ip, cancellationToken);
            return AuthOutcome<SessionTokens>.Fail(AuthError.InvalidRefreshToken);
        }

        if (stored.ExpiresAt <= Now)
            return AuthOutcome<SessionTokens>.Fail(AuthError.InvalidRefreshToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken);
        if (user is null || !user.IsActive)
            return AuthOutcome<SessionTokens>.Fail(AuthError.InvalidRefreshToken);

        // Los permisos se recalculan en cada renovación: un cambio de rol o una baja surte efecto en 15 minutos.
        Membership? membership = null;
        if (stored.CompanyId is int companyId)
        {
            membership = (await LoadMembershipsAsync(user.Id, cancellationToken)).FirstOrDefault(m => m.CompanyId == companyId);
            if (membership is null)
                return AuthOutcome<SessionTokens>.Fail(AuthError.InvalidRefreshToken);
        }
        else if (!user.IsSuperAdmin)
        {
            return AuthOutcome<SessionTokens>.Fail(AuthError.InvalidRefreshToken);
        }

        var session = await IssueSessionAsync(user, membership, ip, cancellationToken, replacing: stored, audit: false);
        return AuthOutcome<SessionTokens>.Ok(session);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = AuthTokenService.HashOpaqueToken(refreshToken);
        await db.RefreshTokens
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), cancellationToken);
    }

    public async Task<AuthOutcome<SessionTokens>> SwitchCompanyAsync(int userId, int companyId, string? ip, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var membership = user is { IsActive: true }
            ? (await LoadMembershipsAsync(userId, cancellationToken)).FirstOrDefault(m => m.CompanyId == companyId)
            : null;
        if (user is null || membership is null)
            return AuthOutcome<SessionTokens>.Fail(AuthError.CompanyNotAllowed);

        return AuthOutcome<SessionTokens>.Ok(await IssueSessionAsync(user, membership, ip, cancellationToken));
    }

    public async Task<CurrentSession?> GetSessionAsync(int userId, int? companyId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
            return null;

        var memberships = await LoadMembershipsAsync(userId, cancellationToken);
        var current = companyId is int id ? memberships.FirstOrDefault(m => m.CompanyId == id) : null;

        return new CurrentSession(
            user.Id, user.Name, user.Email,
            current?.CompanyId, current?.CompanyName, current?.Role,
            user.IsSuperAdmin,
            current?.Permissions ?? [],
            memberships.Select(m => new CompanyOption(m.CompanyId, m.CompanyName, m.Role)).ToList());
    }

    public async Task RequestPasswordResetAsync(string email, string? ip, CancellationToken cancellationToken)
    {
        var normalized = email.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized && u.IsActive, cancellationToken);
        if (user is null)
            return;

        var (token, hash) = AuthTokenService.NewOpaqueToken();
        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = hash,
            ExpiresAt = Now + options.PasswordResetLifetime,
            IpAddress = ip,
        });
        await db.SaveChangesAsync(cancellationToken);

        await mailer.SendAsync(
            user.Email,
            "Recuperar contraseña de MonitoreoTI",
            $"Hola, {user.Name}.\n\nPara crear una contraseña nueva abre este enlace (vence en " +
            $"{options.PasswordResetLifetime.TotalMinutes:0} minutos):\n{options.PasswordResetUrl}{token}\n\n" +
            "Si no lo pediste, ignora este correo: tu contraseña no cambia.",
            cancellationToken);
    }

    public async Task<AuthOutcome<bool>> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        if (!PasswordRules.IsStrong(newPassword))
            return AuthOutcome<bool>.Fail(AuthError.WeakPassword);

        var hash = AuthTokenService.HashOpaqueToken(token);
        var now = Now;

        // Marcarlo usado en la misma sentencia que lo valida: dos clics simultáneos no lo usan dos veces.
        var claimed = await db.PasswordResetTokens
            .Where(t => t.TokenHash == hash && t.UsedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken);
        if (claimed == 0)
            return AuthOutcome<bool>.Fail(AuthError.InvalidResetToken);

        var userId = await db.PasswordResetTokens.Where(t => t.TokenHash == hash).Select(t => t.UserId).FirstAsync(cancellationToken);
        var user = await db.Users.FirstAsync(u => u.Id == userId, cancellationToken);
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, 11);
        user.FailedLoginAttempts = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);

        // Quien tenía la contraseña vieja pierde sus sesiones abiertas.
        await RevokeAllAsync(user.Id, cancellationToken);
        foreach (var membership in (await LoadMembershipsAsync(user.Id, cancellationToken)).DefaultIfEmpty())
            await AuditAsync(membership?.CompanyId, user.Id, "contrasena.restablecida", null, null, cancellationToken);

        return AuthOutcome<bool>.Ok(true);
    }

    private async Task<AuthOutcome<LoginResult>> ContinueAfterTwoFactorAsync(
        User user, IReadOnlyList<Membership> memberships, string? ip, CancellationToken cancellationToken)
    {
        // El superadministrador no entra a ninguna empresa (portal del proveedor, Fase 8).
        if (user.IsSuperAdmin)
            return Ok(new LoginResult(LoginStep.Completed, Session: await IssueSessionAsync(user, null, ip, cancellationToken)));

        return memberships.Count switch
        {
            0 => AuthOutcome<LoginResult>.Fail(AuthError.InvalidCredentials),
            1 => Ok(new LoginResult(LoginStep.Completed, Session: await IssueSessionAsync(user, memberships[0], ip, cancellationToken))),
            _ => Ok(new LoginResult(
                LoginStep.CompanySelectionRequired,
                tokens.CreateChallenge(user.Id, ChallengePurpose.Company),
                Companies: memberships.Select(m => new CompanyOption(m.CompanyId, m.CompanyName, m.Role)).ToList())),
        };
    }

    private async Task<SessionTokens> IssueSessionAsync(
        User user, Membership? membership, string? ip, CancellationToken cancellationToken,
        RefreshToken? replacing = null, bool audit = true)
    {
        var (refresh, refreshHash) = AuthTokenService.NewOpaqueToken();
        var stored = new RefreshToken
        {
            UserId = user.Id,
            CompanyId = membership?.CompanyId,
            TokenHash = refreshHash,
            ExpiresAt = Now + options.RefreshTokenLifetime,
            IpAddress = ip,
        };
        db.RefreshTokens.Add(stored);
        user.LastLoginAt = Now;
        await db.SaveChangesAsync(cancellationToken);

        if (replacing is not null)
        {
            replacing.RevokedAt = Now;
            replacing.ReplacedById = stored.Id;
            await db.SaveChangesAsync(cancellationToken);
        }

        var (access, accessExpires) = tokens.CreateAccessToken(new AccessTokenContent(
            user.Id, user.Name, user.Email, membership?.CompanyId, membership?.Role, user.IsSuperAdmin,
            membership?.Permissions ?? []));

        if (audit)
            await AuditAsync(membership?.CompanyId, user.Id, "login", null, ip, cancellationToken);

        return new SessionTokens(access, accessExpires, refresh, stored.ExpiresAt);
    }

    private async Task<AuthError> RegisterFailureAsync(User user, string action, string? ip, CancellationToken cancellationToken)
    {
        user.FailedLoginAttempts++;
        var locked = user.FailedLoginAttempts >= options.LockoutThreshold;
        if (locked)
        {
            user.LockedUntil = Now + options.LockoutDuration;
            user.FailedLoginAttempts = 0;
        }
        await db.SaveChangesAsync(cancellationToken);

        await AuditAsync(null, user.Id, locked ? "login.bloqueado" : action, null, ip, cancellationToken);
        return locked ? AuthError.LockedOut : AuthError.InvalidCredentials;
    }

    private Task RevokeAllAsync(int userId, CancellationToken cancellationToken)
    {
        var now = Now;
        return db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    private static bool RequiresTwoFactor(User user, IReadOnlyList<Membership> memberships) =>
        user.IsSuperAdmin || memberships.Any(m => m.Permissions.Any(PermissionKeys.RequiringTwoFactor.Contains));

    /// <summary>Empresas activas del usuario con su rol y permisos, cada una leída con su propio contexto.</summary>
    private async Task<IReadOnlyList<Membership>> LoadMembershipsAsync(int userId, CancellationToken cancellationToken)
    {
        var rows = await db.UserCompanies.AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => new { m.CompanyId, m.RoleId })
            .ToListAsync(cancellationToken);

        var result = new List<Membership>();
        foreach (var row in rows)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(row.CompanyId);
            var companyDb = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

            var company = await companyDb.Companies.AsNoTracking()
                .Where(c => c.Id == row.CompanyId && c.Status == CompanyStatus.Active)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(cancellationToken);
            if (company is null)
                continue;

            var role = await companyDb.Roles.AsNoTracking()
                .Where(r => r.Id == row.RoleId)
                .Select(r => new { r.Name, Permissions = r.Permissions.Select(p => p.Permission!.Key).ToList() })
                .FirstOrDefaultAsync(cancellationToken);
            if (role is null)
                continue;

            result.Add(new Membership(row.CompanyId, company, role.Name, role.Permissions.Order().ToList()));
        }

        return result.OrderBy(m => m.CompanyName).ToList();
    }

    /// <summary>Auditoría en el contexto de la empresa (o sin empresa para acciones fuera de una).</summary>
    private async Task AuditAsync(int? companyId, int userId, string action, object? details, string? ip, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        if (companyId is int id)
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(id);
        var auditDb = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        auditDb.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            Action = action,
            Entity = "usuarios",
            EntityId = userId.ToString(),
            Details = details is null ? null : JsonSerializer.Serialize(details),
            SourceIp = ip,
        });
        await auditDb.SaveChangesAsync(cancellationToken);
    }

    private static AuthOutcome<LoginResult> Ok(LoginResult result) => AuthOutcome<LoginResult>.Ok(result);
}

using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>
/// Lo que se exige, además del permiso, para ejecutar en un equipo o abrir una sesión remota (sección 10.1):
/// el plan de la empresa con el módulo remoto y un 2FA de menos de 8 horas (decisión 52).
/// </summary>
public sealed class RemoteAccessGuard(MonitoreoDbContext db, ICurrentUser user, TimeProvider clock)
{
    public async Task<RemoteAccessDenial?> CheckAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var included = await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.StartDate <= today && (s.EndDate == null || s.EndDate >= today))
            .AnyAsync(s => db.PlanModules.Any(m => m.PlanId == s.PlanId && m.Module == ModuleKey.Remote), cancellationToken);
        if (!included)
            return RemoteAccessDenial.ModuleNotIncluded;

        var userId = user.UserId;
        var lastTwoFactor = await db.Users.Where(u => u.Id == userId).Select(u => u.LastTwoFactorAt).FirstOrDefaultAsync(cancellationToken);
        if (lastTwoFactor is null || lastTwoFactor < clock.GetUtcNow().UtcDateTime - RemoteRules.TwoFactorWindow)
            return RemoteAccessDenial.TwoFactorRequired;

        return null;
    }
}

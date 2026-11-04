using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Health;

/// <summary>
/// Correo de alertas críticas (decisión 48): cada alerta crítica abierta se avisa una sola vez (alertas.notificada) a
/// los usuarios activos que gestionan alertas y ven la sucursal del equipo o dispositivo, salvo quien lo desactivó.
/// Un fallo del correo no detiene el motor: se reintenta en el siguiente ciclo.
/// </summary>
public sealed class AlertMailer(IServiceScopeFactory scopes, IEmailSender mailer, TimeProvider clock, ILogger<AlertMailer> logger)
{
    /// <summary>Rol que ve todas las sucursales (como en el panel).</summary>
    private const string AdministratorRole = "Administrador";

    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    /// <returns>Cuántos correos se enviaron.</returns>
    public async Task<int> NotifyCompanyAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var pending = await db.Alerts
            .Where(a => a.Level == AlertLevel.Critical && a.Status != AlertStatus.Resolved && a.NotifiedAt == null)
            .Select(a => new
            {
                Alert = a,
                Machine = db.Machines.Where(m => m.Id == a.MachineId).Select(m => new { m.Name, m.BranchId }).FirstOrDefault(),
                Device = db.NetworkDevices.Where(d => d.Id == a.DeviceId).Select(d => new { d.Name, d.BranchId }).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
            return 0;

        var managers = await db.RolePermissions
            .Join(db.Permissions.Where(p => p.Key == PermissionKeys.AlertsManage), rp => rp.PermissionId, p => p.Id, (rp, p) => rp.RoleId)
            .ToListAsync(cancellationToken);
        var recipients = await db.UserCompanies.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.IsActive && managers.Contains(m.RoleId))
            .Join(db.Users.Where(u => u.IsActive && u.NotifyByEmail), m => m.UserId, u => u.Id,
                (m, u) => new { u.Id, u.Email, Admin = db.Roles.Any(r => r.Id == m.RoleId && r.Name == AdministratorRole) })
            .ToListAsync(cancellationToken);
        var recipientIds = recipients.Select(r => r.Id).ToList();
        var branchesByUser = (await db.UserBranches.AsNoTracking().Where(b => recipientIds.Contains(b.UserId)).ToListAsync(cancellationToken))
            .GroupBy(b => b.UserId).ToDictionary(g => g.Key, g => g.Select(b => b.BranchId).ToHashSet());
        var branchNames = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        var sent = 0;
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var item in pending)
        {
            var branchId = item.Machine?.BranchId ?? item.Device?.BranchId;
            var to = recipients
                .Where(r => r.Admin || branchId is null || !branchesByUser.TryGetValue(r.Id, out var mine) || mine.Contains(branchId.Value))
                .Select(r => r.Email).Distinct().ToList();
            try
            {
                var subject = $"[MonitoreoTI] Crítico: {item.Alert.Message}";
                var body = Body(item.Alert.Message, item.Machine?.Name, item.Device?.Name,
                    branchId is int b ? branchNames.GetValueOrDefault(b) : null, item.Alert.Detail, item.Alert.Advice, item.Alert.CreatedAt);
                foreach (var address in to)
                {
                    await mailer.SendAsync(address, subject, body, cancellationToken);
                    sent++;
                }
                // Sin destinatarios también queda marcada: no se vuelve a revisar en cada ciclo.
                item.Alert.NotifiedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "No se pudo enviar el correo de la alerta {AlertId}; se reintentará.", item.Alert.Id);
            }
        }
        return sent;
    }

    private static string Body(string message, string? machine, string? device, string? branch, string? detail, string? advice, DateTime createdAt)
    {
        var text = new StringBuilder();
        text.AppendLine(message).AppendLine();
        if (machine is not null)
            text.AppendLine($"Equipo: {machine}");
        if (device is not null)
            text.AppendLine($"Dispositivo de red: {device}");
        if (branch is not null)
            text.AppendLine($"Sucursal: {branch}");
        text.AppendLine($"Detectada: {createdAt.ToString("dd/MM/yyyy HH:mm", Es)} (UTC)");
        if (detail is not null)
            text.AppendLine().AppendLine(detail);
        if (advice is not null)
            text.AppendLine().AppendLine($"Qué hacer: {advice}");
        text.AppendLine().AppendLine("Abra el panel de MonitoreoTI para atenderla.")
            .AppendLine("Para dejar de recibir estos correos, desactívelos en el menú de su usuario del panel.");
        return text.ToString();
    }
}

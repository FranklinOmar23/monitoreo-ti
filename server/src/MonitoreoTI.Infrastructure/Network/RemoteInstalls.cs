using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Remote;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// "Instalar agente" desde Direcciones IP (decisión 77). Cada IP lleva su propio token de registro de un solo uso (vence
/// en 2 h, sin aprobación: lo pidió un técnico con permiso de instaladores y 2FA) y la cuenta del dominio sellada para
/// el recolector de la subred. La clave no se guarda ni se audita; la auditoría dice quién, dónde y con qué cuenta.
/// </summary>
public sealed class RemoteInstalls(
    MonitoreoDbContext db, NetworkQueries queries, IAuditTrail audit, ICurrentUser user, IRemoteInstallDispatcher dispatcher,
    AgentReleases releases, IConfiguration configuration, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<Subnet?> SubnetAsync(int id, CancellationToken ct)
    {
        var allowed = await queries.AllowedBranchesAsync(ct);
        return await db.Subnets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && (allowed == null || allowed.Contains(s.BranchId)), ct);
    }

    public async Task<CommandResult> StartAsync(int subnetId, RemoteInstallInput input, string fallbackServerUrl, CancellationToken ct)
    {
        var subnet = await SubnetAsync(subnetId, ct);
        if (subnet is null || !IpRules.TryParse(subnet.Network, out var network))
            return CommandResult.NotFound();
        if (subnet.CollectorAgentId is not int agentId)
            return CommandResult.Invalid("Asigne un recolector a la subred: es el equipo que hace la instalación.");

        var agent = await db.Agents.AsNoTracking().Where(a => a.Id == agentId)
            .Select(a => new { a.Version, a.TokenHash, Name = db.Machines.Where(m => m.Id == a.MachineId).Select(m => m.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (agent is null)
            return CommandResult.Invalid("El recolector ya no existe.");
        if (!RemoteInstallRules.SupportsInstall(agent.Version))
            return CommandResult.Conflict(
                $"El agente de {agent.Name ?? "el recolector"} es la versión {agent.Version ?? "?"}; instalar en otros equipos necesita la {RemoteInstallRules.InstallerVersion} o superior.");

        var (release, problem) = releases.Current();
        if (release is null)
            return CommandResult.Conflict(problem ?? "El instalador del agente no está publicado en el servidor.");

        var ips = new List<string>();
        foreach (var text in input.Ips.Distinct())
        {
            if (!IpRules.TryParse(text, out var ip) || !IpRules.Contains(network, subnet.Prefix, ip))
                return CommandResult.Invalid($"{text} no es de la subred {subnet.Network}/{subnet.Prefix}.");
            ips.Add(IpRules.ToText(ip));
        }
        var since = Now - RemoteInstallRules.StaleAfter;
        var busy = await db.RemoteInstalls.AsNoTracking()
            .Where(r => r.SubnetId == subnetId && ips.Contains(r.Ip) && r.UpdatedAt > since
                && r.Status != RemoteInstallRules.Registered && r.Status != RemoteInstallRules.Failed && r.Status != RemoteInstallRules.Skipped)
            .Select(r => r.Ip).ToListAsync(ct);
        if (busy.Count > 0)
            return CommandResult.Conflict($"Ya se está instalando en {string.Join(", ", busy)}.");

        var account = input.Cuenta.Trim();
        var now = Now;
        var batch = new List<(RemoteInstall Install, string Token)>();
        foreach (var ip in ips)
        {
            var (token, hash) = AgentTokens.Generate(user.CompanyId);
            var enrollment = new EnrollmentToken
            {
                BranchId = subnet.BranchId,
                Name = $"Instalación remota {ip}",
                TokenHash = hash,
                ExpiresAt = now + RemoteInstallRules.TokenLifetime,
                MaxUses = 1,
                RequiresApproval = false,
                CreatedById = user.UserId,
            };
            db.EnrollmentTokens.Add(enrollment);
            await db.SaveChangesAsync(ct);
            var install = new RemoteInstall
            {
                SubnetId = subnetId,
                Ip = ip,
                CollectorAgentId = agentId,
                UserId = user.UserId,
                Account = account,
                EnrollmentTokenId = enrollment.Id,
                Status = RemoteInstallRules.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.RemoteInstalls.Add(install);
            batch.Add((install, token));
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("agente.instalacion_remota", "subredes", subnetId.ToString(), null,
            new { ips, cuenta = account, recolectorId = agentId, version = release.Version }, ct);

        // La cuenta y los tokens se sellan para el recolector: solo él los abre, y no quedan en el servidor.
        var sealedAccount = SnmpCredentialSeal.Seal(agent.TokenHash, JsonSerializer.Serialize(new { usuario = account, clave = input.Clave }));
        var server = (configuration["Instaladores:UrlServidor"] ?? fallbackServerUrl).TrimEnd('/');
        var offer = new AgentReleaseOffer(release.Version, AgentDownloadPath, release.Sha256, release.Size, release.Signature);
        var unreachable = false;
        foreach (var (install, token) in batch)
        {
            var request = new RemoteInstallRequest(install.Id, install.Ip, sealedAccount, server,
                SnmpCredentialSeal.Seal(agent.TokenHash, JsonSerializer.Serialize(new { token })), offer);
            if (unreachable || !await dispatcher.SendAsync(user.CompanyId, agentId, request, ct))
            {
                unreachable = true;
                install.Status = RemoteInstallRules.Failed;
                install.Detail = $"El recolector {agent.Name} no está conectado.";
                install.UpdatedAt = Now;
            }
        }
        if (unreachable)
        {
            var tokens = batch.Where(b => b.Install.Status == RemoteInstallRules.Failed).Select(b => b.Install.EnrollmentTokenId).ToList();
            await db.EnrollmentTokens.Where(t => tokens.Contains(t.Id)).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, Now), ct);
            await db.SaveChangesAsync(ct);
            return CommandResult.Conflict($"El recolector {agent.Name} no está conectado: inténtelo cuando vuelva a estar en línea.");
        }
        return CommandResult.Ok(batch[0].Install.Id);
    }

    /// <summary>Lo mismo que el actualizador: el recolector baja el instalador publicado con su credencial.</summary>
    public const string AgentDownloadPath = "/agent/actualizacion";

    /// <summary>Las instalaciones de las últimas 24 h de la subred; las que se quedaron a medias se muestran como error.</summary>
    public async Task<IReadOnlyList<RemoteInstallItem>?> ListAsync(int subnetId, CancellationToken ct)
    {
        if (await SubnetAsync(subnetId, ct) is null)
            return null;
        var now = Now;
        var since = now.AddHours(-24);
        var rows = await db.RemoteInstalls.AsNoTracking()
            .Where(r => r.SubnetId == subnetId && r.CreatedAt >= since)
            .OrderByDescending(r => r.Id)
            .Select(r => new
            {
                Row = r,
                UserName = db.Users.Where(u => u.Id == r.UserId).Select(u => u.Name).FirstOrDefault(),
                MachineName = db.Machines.Where(m => m.Id == r.MachineId).Select(m => m.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.Select(x =>
        {
            var r = x.Row;
            var stale = !RemoteInstallRules.IsFinal(r.Status) && r.Status != RemoteInstallRules.Installed
                && r.UpdatedAt < now - RemoteInstallRules.StaleAfter;
            var lost = r.Status == RemoteInstallRules.Installed && r.UpdatedAt < now - RemoteInstallRules.StaleAfter;
            return new RemoteInstallItem(r.Id, r.Ip,
                stale || lost ? RemoteInstallRules.Failed : r.Status,
                stale ? "El recolector dejó de informar a mitad de la instalación."
                    : lost ? "Se instaló, pero el equipo no se registró: revise que llegue al servidor (puerto y certificado)."
                    : r.Detail,
                r.OperatingSystem, r.Account, x.UserName, r.MachineId, x.MachineName, r.CreatedAt, r.UpdatedAt);
        }).ToList();
    }
}

/// <summary>El lado del gateway: lo que informa el recolector sobre cada instalación.</summary>
public sealed class RemoteInstallStatus(IServiceScopeFactory scopes, ILogger<RemoteInstallStatus> logger) : IRemoteInstallStatus
{
    public async Task SaveAsync(AgentIdentity identity, InstalacionEstadoData data, DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        if (!RemoteInstallRules.AgentStates.Contains(data.Estado))
            return;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(identity.CompanyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var install = await db.RemoteInstalls.FirstOrDefaultAsync(
            r => r.Id == data.InstalacionId && r.CollectorAgentId == identity.AgentId, cancellationToken);
        if (install is null)
        {
            logger.LogWarning("Agente {AgentId}: estado de la instalación {InstallId}, que no le corresponde; se ignora.",
                identity.AgentId, data.InstalacionId);
            return;
        }
        // Ya se registró (el equipo llegó antes que el aviso del recolector) o terminó: no se retrocede.
        if (RemoteInstallRules.IsFinal(install.Status))
            return;

        install.Status = data.Estado;
        install.Detail = Cut(data.Detalle, 400);
        install.OperatingSystem = Cut(data.Sistema, 100) ?? install.OperatingSystem;
        install.UpdatedAt = receivedAtUtc;
        if (data.Estado is RemoteInstallRules.Failed or RemoteInstallRules.Skipped && install.EnrollmentTokenId is int tokenId)
            await db.EnrollmentTokens.Where(t => t.Id == tokenId && t.RevokedAt == null && t.Uses == 0)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, receivedAtUtc), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? Cut(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value.Trim() : value[..max];
}

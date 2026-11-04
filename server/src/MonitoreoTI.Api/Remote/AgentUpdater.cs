using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Api.Agents;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Remote;

namespace MonitoreoTI.Api.Remote;

/// <summary>
/// Actualización automática del agente (6.6): a los agentes conectados con una versión anterior a la publicada se les
/// manda "actualizar_agente" (versión, ruta de descarga, SHA-256 y firma del producto), por tandas de
/// <see cref="BatchSize"/> por empresa. Un agente en curso cuenta hasta que vuelve con la versión nueva o pasan
/// <see cref="InFlightTimeout"/>; si no vuelve, se le ofrece otra vez en la siguiente pasada.
/// </summary>
public sealed class AgentUpdater(
    AgentConnectionRegistry registry, AgentReleases releases, IServiceScopeFactory scopes, TimeProvider clock, ILogger<AgentUpdater> logger)
{
    public const int BatchSize = 10;
    public static readonly TimeSpan InFlightTimeout = TimeSpan.FromMinutes(15);
    public const string DownloadPath = "/agent/actualizacion";

    // agente -> (empresa, cuándo se le mandó)
    private readonly ConcurrentDictionary<int, (int CompanyId, DateTime SentAt)> _inFlight = new();

    public bool IsUpdating(int agentId) =>
        _inFlight.TryGetValue(agentId, out var f) && clock.GetUtcNow().UtcDateTime - f.SentAt < InFlightTimeout;

    /// <summary>Tras la bienvenida: si la empresa actualiza sola y el agente está atrasado, se le ofrece (si hay cupo).</summary>
    public async Task OfferOnConnectAsync(AgentConnection connection, string? version, CancellationToken cancellationToken)
    {
        var (release, _) = releases.Current();
        if (release is null)
            return;
        if (!VersionRules.IsOlder(version, release.Version))
        {
            _inFlight.TryRemove(connection.Identity.AgentId, out _);
            return;
        }
        if (await AutoUpdateAsync(connection.Identity.CompanyId, cancellationToken))
            await OfferAsync(connection, release, cancellationToken);
    }

    /// <summary>Ofrece la versión a los agentes conectados y atrasados de la empresa, sin pasar del cupo. Devuelve a cuántos.</summary>
    public async Task<int> RolloutAsync(int companyId, bool onlyIfAutomatic, CancellationToken cancellationToken)
    {
        var (release, _) = releases.Current();
        if (release is null || (onlyIfAutomatic && !await AutoUpdateAsync(companyId, cancellationToken)))
            return 0;

        var connected = registry.All().Where(c => c.Identity.CompanyId == companyId).ToDictionary(c => c.Identity.AgentId);
        if (connected.Count == 0)
            return 0;
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
        var ids = connected.Keys.ToList();
        var versions = await db.Agents.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new { a.Id, a.Version }).ToListAsync(cancellationToken);

        var offered = 0;
        foreach (var agent in versions.Where(a => VersionRules.IsOlder(a.Version, release.Version)))
        {
            if (await OfferAsync(connected[agent.Id], release, cancellationToken))
                offered++;
        }
        return offered;
    }

    public async Task<UpdateStatus> StatusAsync(MonitoreoDbContext db, ICurrentUser user, CancellationToken cancellationToken)
    {
        var (release, problem) = releases.Current();
        var auto = await db.Companies.Where(c => c.Id == user.CompanyId).Select(c => c.AutoUpdateAgents).FirstOrDefaultAsync(cancellationToken);
        var allowed = await user.AllowedBranchIdsAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking()
            .Where(a => !a.IsRevoked && a.IsApproved)
            .Join(db.Machines.Where(m => m.Status != MachineStatus.Retired && (allowed == null || allowed.Contains(m.BranchId))),
                a => a.MachineId, m => m.Id, (a, m) => new { a.Id, a.Version, MachineId = m.Id, m.Name, Branch = m.Branch!.Name })
            .ToListAsync(cancellationToken);

        var outdated = release is null ? [] : agents.Where(a => VersionRules.IsOlder(a.Version, release.Version)).ToList();
        var items = outdated.OrderBy(a => a.Name)
            .Take(200)
            .Select(a => new AgentVersionItem(a.MachineId, a.Name, a.Branch, a.Version, registry.Get(a.Id) is not null, IsUpdating(a.Id)))
            .ToList();
        return new UpdateStatus(release?.Version, problem, auto, agents.Count, agents.Count - outdated.Count, outdated.Count,
            outdated.Count(a => IsUpdating(a.Id)), items);
    }

    private async Task<bool> OfferAsync(AgentConnection connection, AgentRelease release, CancellationToken cancellationToken)
    {
        var identity = connection.Identity;
        var now = clock.GetUtcNow().UtcDateTime;
        if (IsUpdating(identity.AgentId))
            return false;
        var busy = _inFlight.Count(f => f.Value.CompanyId == identity.CompanyId && now - f.Value.SentAt < InFlightTimeout);
        if (busy >= BatchSize)
            return false;

        _inFlight[identity.AgentId] = (identity.CompanyId, now);
        try
        {
            await connection.SendAsync("actualizar_agente", new
            {
                version = release.Version,
                url = DownloadPath,
                sha256 = release.Sha256,
                tamano = release.Size,
                firma = release.Signature,
            }, cancellationToken);
            logger.LogInformation("Agente {AgentId}: se le ofreció la versión {Version}.", identity.AgentId, release.Version);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            _inFlight.TryRemove(identity.AgentId, out _);
            return false;
        }
    }

    private async Task<bool> AutoUpdateAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
        return await db.Companies.Where(c => c.Id == companyId).Select(c => c.AutoUpdateAgents).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>GET /agent/actualizacion: el instalador publicado, solo para agentes con credencial válida.</summary>
    public static async Task<IResult> DownloadAsync(HttpContext http, IAgentSessions sessions, AgentReleases releases)
    {
        var header = http.Request.Headers.Authorization.ToString();
        var credential = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (await sessions.AuthenticateAsync(credential, http.RequestAborted) is null)
            return Results.Unauthorized();
        var (release, _) = releases.Current();
        return release is null || releases.InstallerPath is not { } path
            ? Results.NotFound()
            : Results.File(path, "application/vnd.microsoft.portable-executable", $"MonitoreoTI-Agente-{release.Version}.exe", enableRangeProcessing: true);
    }
}

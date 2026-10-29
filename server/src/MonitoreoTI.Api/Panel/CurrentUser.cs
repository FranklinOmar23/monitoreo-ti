using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Api.Auth;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Api.Panel;

/// <summary>Usuario de la petición a partir del token (los endpoints del panel exigen sesión con empresa).</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor, MonitoreoDbContext db) : ICurrentUser
{
    /// <summary>Rol que ve todas las sucursales aunque tenga algunas asignadas.</summary>
    public const string AdministratorRole = "Administrador";

    private ClaimsPrincipal? _principal;
    private IReadOnlyCollection<int>? _branches;
    private bool _branchesLoaded;

    /// <summary>Fuera de una petición HTTP (hub de SignalR) el usuario se indica explícitamente.</summary>
    public void UsePrincipal(ClaimsPrincipal principal) => _principal = principal;

    private ClaimsPrincipal Principal => _principal ?? accessor.HttpContext?.User
        ?? throw new InvalidOperationException("No hay petición en curso.");

    public int UserId => AuthEndpoints.UserId(Principal);

    public int CompanyId => AuthEndpoints.CompanyId(Principal)
        ?? throw new InvalidOperationException("La sesión no tiene empresa.");

    public string? Role => Principal.FindFirstValue(AuthClaims.Role);

    public bool HasPermission(string key) => Principal.HasClaim(AuthClaims.Permission, key);

    public async Task<IReadOnlyCollection<int>?> AllowedBranchIdsAsync(CancellationToken cancellationToken)
    {
        if (_branchesLoaded)
            return _branches;

        if (Role != AdministratorRole)
        {
            var userId = UserId;
            var assigned = await db.UserBranches.AsNoTracking().Where(b => b.UserId == userId).Select(b => b.BranchId).ToListAsync(cancellationToken);
            _branches = assigned.Count > 0 ? assigned : null;
        }

        _branchesLoaded = true;
        return _branches;
    }
}

/// <summary>Auditoría de escrituras con el usuario, la empresa (contexto de la petición) y la IP.</summary>
public sealed class AuditTrail(IHttpContextAccessor accessor, MonitoreoDbContext db, ICurrentUser user) : IAuditTrail
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(string action, string entity, string? entityId, object? before, object? after, CancellationToken cancellationToken)
    {
        db.AuditLogs.Add(new AuditLog
        {
            UserId = user.UserId,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Details = before is null && after is null ? null : JsonSerializer.Serialize(new { antes = before, despues = after }, Json),
            SourceIp = accessor.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

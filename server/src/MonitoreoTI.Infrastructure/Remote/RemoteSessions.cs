using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>
/// Sesiones remotas desde el panel (sección 10.1): abrir (crea el registro y la registra en el relay), cerrar e
/// historial (W-34). El relay solo reenvía bytes; aquí se decide quién puede abrir qué.
/// </summary>
public sealed class RemoteSessions(MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit, IRemoteRelay relay, TimeProvider clock)
{
    public async Task<(SessionTicket? Ticket, CommandResult? Error)> OpenAsync(SessionInput input, string? sourceIp, CancellationToken ct)
    {
        if (!user.HasPermission(SessionRules.Permission(input.Tipo)))
            return (null, CommandResult.Invalid("No tiene permiso para ese tipo de sesión."));

        var allowed = await user.AllowedBranchIdsAsync(ct);
        var machine = await db.Machines.AsNoTracking()
            .Where(m => m.Id == input.MaquinaId && m.Status != MachineStatus.Retired && (allowed == null || allowed.Contains(m.BranchId)))
            .Where(m => db.Agents.Any(a => a.MachineId == m.Id && !a.IsRevoked && a.IsApproved))
            .Select(m => new { m.Id, m.Name, AgentVersion = db.Agents.Where(a => a.MachineId == m.Id && !a.IsRevoked).Select(a => a.Version).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (machine is null)
            return (null, CommandResult.Invalid("El equipo no existe, no tiene agente o no tiene acceso a él."));
        // El agente clásico anterior a la 0.2.0 (decisiones 78 y 84) no tiene sesiones remotas: ignoraría la orden y el
        // panel se quedaría esperando. Se dice claro antes de abrir nada.
        if (SessionRules.IsClassicWithoutSessions(machine.AgentVersion))
            return (null, CommandResult.Conflict(
                $"{machine.Name} tiene el agente clásico {machine.AgentVersion}, sin escritorio remoto, terminal ni explorador de archivos. " +
                "Reinstálele el agente (instalación remota o manual) para pasarlo a la versión 0.2.0 o posterior, que sí los tiene."));

        var session = db.RemoteSessions.Add(new RemoteSession
        {
            MachineId = machine.Id,
            UserId = user.UserId,
            Type = ParseType(input.Tipo),
            StartedAt = clock.GetUtcNow().UtcDateTime,
            SourceIp = sourceIp,
            Reason = string.IsNullOrWhiteSpace(input.Motivo) ? null : input.Motivo.Trim(),
            ViewOnly = input.Tipo == "escritorio" && input.SoloVer,
        }).Entity;
        await db.SaveChangesAsync(ct);

        var userId = user.UserId;
        var technician = await db.Users.Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct) ?? "Un técnico";
        var companyId = user.CompanyId;
        // Si la empresa lo exige se pide siempre; si no, el técnico decide en cada sesión.
        var companyAsks = await db.Companies.Where(c => c.Id == companyId).Select(c => c.AskRemoteConsent).FirstOrDefaultAsync(ct);
        var desktop = input.Tipo == "escritorio";
        var opened = await relay.OpenAsync(new RelayOpening(
            session.Id, user.CompanyId, machine.Id, user.UserId, input.Tipo, technician, desktop && (companyAsks || input.PedirPermiso),
            session.ViewOnly, desktop && user.HasPermission("remoto.archivos")), ct);
        if (opened is null)
        {
            // Sin agente conectado no hubo sesión: no queda en el historial.
            db.RemoteSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return (null, CommandResult.Conflict("El equipo no está conectado ahora. Intente cuando aparezca en línea."));
        }

        await audit.RecordAsync("sesion.abrir", "maquinas", machine.Id.ToString(), null,
            new { sesionId = session.Id, tipo = input.Tipo, motivo = session.Reason, soloVer = session.ViewOnly }, ct);
        var (token, expires) = opened.Value;
        return (new SessionTicket(session.Id, input.Tipo, machine.Id, machine.Name, SessionRules.RelayPath(session.Id), token, expires), null);
    }

    /// <summary>Cierra la propia sesión (o cualquiera, el Administrador).</summary>
    public async Task<CommandResult> CloseAsync(long id, CancellationToken ct)
    {
        var session = await (await VisibleAsync(ct)).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
            return CommandResult.NotFound();
        if (session.UserId != user.UserId && user.Role != "Administrador")
            return CommandResult.Conflict("Solo quien abrió la sesión (o un administrador) puede cerrarla.");

        await relay.CloseAsync(user.CompanyId, id, "cerrada desde el panel", ct);
        return CommandResult.Ok(id);
    }

    public async Task<PagedResult<SessionItem>> ListAsync(SessionFilter filter, CancellationToken ct)
    {
        var query = await VisibleAsync(ct);
        if (filter.MachineId is int machine)
            query = query.Where(s => s.MachineId == machine);
        if (filter.UserId is int userId)
            query = query.Where(s => s.UserId == userId);
        if (TryParseText<RemoteSessionType>(filter.Type, out var type))
            query = query.Where(s => s.Type == type);
        if (filter.ViewOnly is bool viewOnly)
            query = query.Where(s => s.ViewOnly == viewOnly);
        if (filter.Active is bool active)
            query = active ? query.Where(s => s.EndedAt == null) : query.Where(s => s.EndedAt != null);
        var (from, to) = Paging.DateRange(filter.From, filter.To);
        if (from is not null)
            query = query.Where(s => s.StartedAt >= from);
        if (to is not null)
            query = query.Where(s => s.StartedAt < to);

        var (page, limit) = Paging.Normalize(filter.Page, filter.Limit);
        var rows = await query.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id,
                s.Type,
                s.MachineId,
                s.UserId,
                s.StartedAt,
                s.EndedAt,
                s.SourceIp,
                s.Reason,
                s.ViewOnly,
                Machine = db.Machines.Where(m => m.Id == s.MachineId).Select(m => new { m.Name, Branch = m.Branch!.Name }).First(),
                UserName = db.Users.Where(u => u.Id == s.UserId).Select(u => u.Name).FirstOrDefault(),
            })
            .ToPageAsync(page, limit, ct);

        return rows.Map(s => new SessionItem(
            s.Id, Text(s.Type), s.MachineId, s.Machine.Name, s.Machine.Branch, s.UserId, s.UserName ?? "", s.StartedAt, s.EndedAt,
            s.EndedAt is DateTime end ? (int)(end - s.StartedAt).TotalSeconds : null, s.SourceIp,
            s.EndedAt is null && relay.IsActive(s.Id), s.Reason, s.ViewOnly));
    }

    private async Task<IQueryable<RemoteSession>> VisibleAsync(CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var machines = db.Machines.Where(m => allowed == null || allowed.Contains(m.BranchId)).Select(m => m.Id);
        return db.RemoteSessions.Where(s => machines.Contains(s.MachineId));
    }

    internal static RemoteSessionType ParseType(string value) => value switch
    {
        "terminal" => RemoteSessionType.Terminal,
        "archivos" => RemoteSessionType.Files,
        _ => RemoteSessionType.Desktop,
    };
}

/// <summary>Registro del final de las sesiones desde el relay (sin petición HTTP): duración y auditoría.</summary>
public sealed class RemoteSessionLog(IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task EndAsync(int companyId, long sessionId, string reason, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var session = await db.RemoteSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.EndedAt == null, cancellationToken);
        if (session is null)
            return;
        Close(db, session, reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static readonly HashSet<string> FileActions = ["descargar", "subir", "crear_carpeta", "renombrar", "eliminar"];

    /// <summary>Acciones del sistema desde el escritorio remoto (menú "Más"): también quedan en auditoría.</summary>
    private static readonly HashSet<string> DesktopActions =
        ["bloquear_entrada", "desbloquear_entrada", "pantalla_negra", "quitar_pantalla_negra", "cambiar_usuario", "cerrar_sesion", "reiniciar"];

    /// <summary>
    /// Auditoría de lo hecho con archivos en una sesión, a nombre del técnico que la abrió. Solo cuenta si la sesión es
    /// de archivos y del equipo de ese agente. Devuelve false si se descartó.
    /// </summary>
    public async Task<bool> RecordActivityAsync(AgentIdentity identity, SessionActivityData data, CancellationToken cancellationToken)
    {
        var fileAction = FileActions.Contains(data.Accion);
        if (!fileAction && !DesktopActions.Contains(data.Accion))
            return false;
        if (fileAction && string.IsNullOrWhiteSpace(data.Ruta))
            return false;

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(identity.CompanyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var session = await db.RemoteSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == data.SesionId && s.MachineId == identity.MachineId
                && (s.Type == RemoteSessionType.Files || s.Type == RemoteSessionType.Desktop), cancellationToken);
        // Las acciones del sistema solo existen en el escritorio.
        if (session is null || (!fileAction && session.Type != RemoteSessionType.Desktop))
            return false;

        db.AuditLogs.Add(new AuditLog
        {
            UserId = session.UserId,
            Action = fileAction ? $"archivo.{data.Accion}" : $"escritorio.{data.Accion}",
            Entity = "maquinas",
            EntityId = identity.MachineId.ToString(),
            Details = JsonSerializer.Serialize(new
            {
                despues = new
                {
                    sesionId = session.Id,
                    ruta = string.IsNullOrEmpty(data.Ruta) ? null : Cut(data.Ruta),
                    destino = data.Destino is null ? null : Cut(data.Destino),
                    tamano = data.Tamano,
                },
            }),
            SourceIp = session.SourceIp,
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string Cut(string path) => path.Length > 1000 ? path[..1000] : path;

    /// <summary>Sesiones sin cerrar que ya no están en el relay (el servidor se reinició a mitad): se cierran ahora.</summary>
    public async Task<int> CloseOrphansAsync(int companyId, Func<long, bool> isActive, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var limit = clock.GetUtcNow().UtcDateTime - SessionRules.TokenLifetime - TimeSpan.FromMinutes(1);
        var open = await db.RemoteSessions.Where(s => s.EndedAt == null && s.StartedAt < limit).ToListAsync(cancellationToken);
        var orphans = open.Where(s => !isActive(s.Id)).ToList();
        foreach (var session in orphans)
            Close(db, session, "el servidor perdió la conexión");
        await db.SaveChangesAsync(cancellationToken);
        return orphans.Count;
    }

    private void Close(MonitoreoDbContext db, RemoteSession session, string reason)
    {
        session.EndedAt = clock.GetUtcNow().UtcDateTime;
        db.AuditLogs.Add(new AuditLog
        {
            UserId = session.UserId,
            Action = "sesion.cerrar",
            Entity = "maquinas",
            EntityId = session.MachineId.ToString(),
            Details = JsonSerializer.Serialize(new
            {
                despues = new
                {
                    sesionId = session.Id,
                    tipo = Text(session.Type),
                    duracionS = (int)(session.EndedAt.Value - session.StartedAt).TotalSeconds,
                    motivo = reason,
                },
            }),
            SourceIp = session.SourceIp,
        });
    }
}

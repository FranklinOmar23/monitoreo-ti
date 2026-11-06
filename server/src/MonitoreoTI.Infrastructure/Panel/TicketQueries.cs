using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Lecturas de tickets (W-24 a W-26). Quien gestiona tickets ve los de sus sucursales; quien solo puede crearlos
/// (rol Empleado) ve únicamente los que abrió él. Los tickets sin equipo los ve quien ve todas las sucursales y su autor.
/// </summary>
public sealed class TicketQueries(MonitoreoDbContext db, ICurrentUser user, IAttachmentStore attachments)
{
    /// <summary>Tickets que el usuario puede ver. También la usan las escrituras para no tocar tickets ajenos.</summary>
    public async Task<IQueryable<Ticket>> VisibleTicketsAsync(CancellationToken ct, bool tracking = false)
    {
        var tickets = tracking ? db.Tickets : db.Tickets.AsNoTracking();
        var me = user.UserId;
        if (!user.HasPermission(PermissionKeys.TicketsManage))
            tickets = tickets.Where(t => t.CreatedById == me);

        var branches = await user.AllowedBranchIdsAsync(ct);
        if (branches is null)
            return tickets;
        var machineIds = db.Machines.Where(m => branches.Contains(m.BranchId)).Select(m => m.Id);
        return tickets.Where(t => (t.MachineId != null && machineIds.Contains(t.MachineId.Value)) || (t.MachineId == null && t.CreatedById == me));
    }

    public async Task<TicketList> TicketsAsync(TicketFilter filter, CancellationToken ct)
    {
        // Decisión 83: la vista trae el equipo, la sucursal, las personas y los comentarios en la misma consulta.
        var me = user.UserId;
        var scoped = db.TicketListView.AsNoTracking();
        if (!user.HasPermission(PermissionKeys.TicketsManage))
            scoped = scoped.Where(t => t.CreatedById == me);
        var allowed = await user.AllowedBranchIdsAsync(ct);
        if (allowed is not null)
            scoped = scoped.Where(t => (t.MachineId != null && t.BranchId != null && allowed.Contains(t.BranchId.Value))
                || (t.MachineId == null && t.CreatedById == me));

        if (filter.BranchId is int branchId)
            scoped = scoped.Where(t => t.BranchId == branchId);
        if (filter.MachineId is int machineId)
            scoped = scoped.Where(t => t.MachineId == machineId);
        if (filter.AlertId is long alertId)
            scoped = scoped.Where(t => t.AlertId == alertId);
        if (filter.CreatedById is int createdBy)
            scoped = scoped.Where(t => t.CreatedById == createdBy);
        if (!string.IsNullOrWhiteSpace(filter.Title))
            scoped = scoped.Where(t => t.Title.Contains(filter.Title.Trim()));
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            var number = int.TryParse(q.TrimStart('#'), out var n) ? n : 0;
            scoped = scoped.Where(t => t.Title.Contains(q) || t.Id == number || (t.Machine != null && t.Machine.Contains(q)));
        }
        if (filter.Priority is { } priorityText && TicketValues.Priorities.Contains(priorityText))
        {
            var priority = EnumMemberConverter<TicketPriority>.FromProvider(priorityText);
            scoped = scoped.Where(t => t.Priority == priority);
        }
        var (from, to) = Paging.DateRange(filter.From, filter.To);
        if (from is not null)
            scoped = scoped.Where(t => t.CreatedAt >= from);
        if (to is not null)
            scoped = scoped.Where(t => t.CreatedAt < to);

        var tally = await scoped
            .GroupBy(t => new { t.Status, Mine = t.AssignedToId == me, Unassigned = t.AssignedToId == null })
            .Select(g => new { g.Key.Status, g.Key.Mine, g.Key.Unassigned, Count = g.Count() })
            .ToListAsync(ct);
        int Of(TicketStatus s) => tally.Where(t => t.Status == s).Sum(t => t.Count);
        bool Active(TicketStatus s) => s is not TicketStatus.Resolved and not TicketStatus.Closed;
        var counts = new TicketCounts(
            Of(TicketStatus.Open), Of(TicketStatus.InProgress), Of(TicketStatus.OnHold), Of(TicketStatus.Resolved), Of(TicketStatus.Closed),
            tally.Where(t => t.Mine && Active(t.Status)).Sum(t => t.Count),
            tally.Where(t => t.Unassigned && Active(t.Status)).Sum(t => t.Count));

        var query = filter.Assignee switch
        {
            "yo" => scoped.Where(t => t.AssignedToId == me),
            "sin" => scoped.Where(t => t.AssignedToId == null),
            { } id when int.TryParse(id, out var assignee) => scoped.Where(t => t.AssignedToId == assignee),
            _ => scoped,
        };
        var status = filter.Status ?? "activos";
        if (TicketValues.Statuses.Contains(status))
        {
            var value = EnumMemberConverter<TicketStatus>.FromProvider(status);
            query = query.Where(t => t.Status == value);
        }
        else if (status != "todos")
        {
            query = query.Where(t => t.Status != TicketStatus.Closed);
        }

        var (page, limit) = Paging.Normalize(filter.Page, filter.Limit);
        // Por prioridad real (urgente, alta, media, baja) y los más nuevos primero.
        var rows = await query.OrderByDescending(t => t.PriorityOrder).ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .ToPageAsync(page, limit, ct);

        var items = rows.Map(r => new TicketListItem(
            r.Id, r.Title, Text(r.Priority), Text(r.Status), r.MachineId, r.Machine, r.Branch, r.AlertId, r.AssignedToId,
            r.AssignedTo, r.CreatedBy ?? "—", r.CreatedAt, r.ClosedAt, r.Comments));
        return new TicketList(items, counts);
    }

    public async Task<TicketDetail?> TicketAsync(int id, CancellationToken ct)
    {
        var row = await (await VisibleTicketsAsync(ct))
            .Where(t => t.Id == id)
            .Select(t => new
            {
                Ticket = t,
                Machine = db.Machines.Where(m => m.Id == t.MachineId)
                    .Select(m => new { m.Name, m.Status, m.BranchId, Branch = m.Branch!.Name }).FirstOrDefault(),
                Alert = db.Alerts.Where(a => a.Id == t.AlertId).Select(a => new { a.Message, a.Status }).FirstOrDefault(),
                AssignedTo = db.Users.Where(u => u.Id == t.AssignedToId).Select(u => u.Name).FirstOrDefault(),
                CreatedBy = db.Users.Where(u => u.Id == t.CreatedById).Select(u => u.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;

        var comments = await db.TicketComments.AsNoTracking().Where(c => c.TicketId == id).OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new TicketCommentItem(c.Id, db.Users.Where(u => u.Id == c.UserId).Select(u => u.Name).FirstOrDefault() ?? "—", c.Text, c.CreatedAt))
            .ToListAsync(ct);
        var files = await db.TicketAttachments.AsNoTracking().Where(a => a.TicketId == id).OrderBy(a => a.CreatedAt)
            .Select(a => new TicketAttachmentItem(a.Id, a.ContentType, a.CreatedAt))
            .ToListAsync(ct);

        var entityId = id.ToString();
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(l => l.Entity == "tickets" && l.EntityId == entityId)
            .OrderBy(l => l.Timestamp)
            .Select(l => new { l.Timestamp, l.Action, l.Details, User = db.Users.Where(u => u.Id == l.UserId).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);

        var t = row.Ticket;
        var history = audit.Select(l => new TicketHistoryItem(l.Timestamp, ActionLabel(l.Action), l.User, DetailOf(l.Action, l.Details))).ToList();
        if (history.Count == 0 || audit[0].Action != "ticket.crear")
            history.Insert(0, new TicketHistoryItem(t.CreatedAt, "Ticket creado", row.CreatedBy, null));

        return new TicketDetail(
            t.Id, t.Title, t.Description, Text(t.Priority), Text(t.Status), t.MachineId, row.Machine?.Name,
            row.Machine is null ? null : Text(row.Machine.Status), row.Machine?.BranchId, row.Machine?.Branch,
            t.AlertId, row.Alert?.Message, row.Alert is null ? null : Text(row.Alert.Status),
            t.CreatedById, row.CreatedBy ?? "—", t.AssignedToId, row.AssignedTo, t.CreatedAt, t.ClosedAt,
            comments, files, history);
    }

    public async Task<TicketAttachmentContent?> AttachmentAsync(int ticketId, int attachmentId, CancellationToken ct)
    {
        if (!await (await VisibleTicketsAsync(ct)).AnyAsync(t => t.Id == ticketId, ct))
            return null;
        var file = await db.TicketAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId && a.TicketId == ticketId, ct);
        if (file is null || await attachments.ReadAsync(file.FileUrl, ct) is not { } content)
            return null;
        return new TicketAttachmentContent(file.Id, file.ContentType ?? "application/octet-stream", Convert.ToBase64String(content));
    }

    /// <summary>Usuarios de la empresa que pueden recibir tickets (su rol tiene tickets.gestionar).</summary>
    public async Task<IReadOnlyList<TechnicianItem>> TechniciansAsync(CancellationToken ct)
    {
        var companyId = user.CompanyId;
        var roles = await db.RolePermissions
            .Join(db.Permissions.Where(p => p.Key == PermissionKeys.TicketsManage), rp => rp.PermissionId, p => p.Id, (rp, p) => rp.RoleId)
            .ToListAsync(ct);
        return await db.UserCompanies.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.IsActive && roles.Contains(m.RoleId))
            .Join(db.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.Name })
            .OrderBy(u => u.Name)
            .Select(u => new TechnicianItem(u.Id, u.Name))
            .ToListAsync(ct);
    }

    private static string ActionLabel(string action) => action switch
    {
        "ticket.crear" => "Ticket creado",
        "ticket.editar" => "Ticket editado",
        "ticket.estado" => "Cambio de estado",
        "ticket.asignar" => "Asignación",
        "ticket.adjuntar" => "Imagen adjunta",
        _ => action,
    };

    /// <summary>"Abierto → En proceso", "Asignado a Ana"… a partir del antes y después de la auditoría.</summary>
    private static string? DetailOf(string action, string? details)
    {
        if (details is null || action is not ("ticket.estado" or "ticket.asignar"))
            return null;
        try
        {
            using var json = JsonDocument.Parse(details);
            string? Read(string side, string field) =>
                json.RootElement.TryGetProperty(side, out var value) && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

            return action == "ticket.estado"
                ? $"{StatusLabel(Read("antes", "estado"))} → {StatusLabel(Read("despues", "estado"))}"
                : Read("despues", "asignado") is { } name ? $"Asignado a {name}" : "Sin asignar";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StatusLabel(string? status) => status switch
    {
        "abierto" => "Abierto",
        "en_proceso" => "En proceso",
        "en_espera" => "En espera",
        "resuelto" => "Resuelto",
        "cerrado" => "Cerrado",
        _ => "—",
    };
}

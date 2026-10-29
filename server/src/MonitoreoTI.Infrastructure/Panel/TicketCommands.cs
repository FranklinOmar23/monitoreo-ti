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
/// Escrituras de tickets. Crear y comentar: quien puede ver el ticket. Asignar, cambiar estado y editar: quien
/// gestiona tickets (el autor puede corregir su ticket mientras siga abierto). Todo queda en auditoría.
/// </summary>
public sealed class TicketCommands(
    MonitoreoDbContext db,
    ICurrentUser user,
    TicketQueries queries,
    IAttachmentStore attachments,
    IAuditTrail audit,
    IRealtimeNotifier notifier,
    TimeProvider clock)
{
    public const int MaxAttachments = 10;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private bool CanManage => user.HasPermission(PermissionKeys.TicketsManage);

    private async Task<Ticket?> FindAsync(int id, CancellationToken ct) =>
        await (await queries.VisibleTicketsAsync(ct, tracking: true)).FirstOrDefaultAsync(t => t.Id == id, ct);

    private async Task<bool> CanSeeMachineAsync(int machineId, CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        return await db.Machines.AnyAsync(m => m.Id == machineId && m.Status != MachineStatus.Retired
            && (allowed == null || allowed.Contains(m.BranchId)), ct);
    }

    private async Task<bool> IsTechnicianAsync(int userId, CancellationToken ct) =>
        (await queries.TechniciansAsync(ct)).Any(t => t.Id == userId);

    private async Task<string?> UserNameAsync(int? userId, CancellationToken ct) =>
        userId is null ? null : await db.Users.Where(u => u.Id == userId).Select(u => u.Name).FirstOrDefaultAsync(ct);

    public async Task<CommandResult> CreateAsync(TicketInput input, CancellationToken ct)
    {
        var machineId = input.MaquinaId;
        Domain.Entities.Monitoring.Alert? alert = null;
        if (input.AlertaId is long alertId)
        {
            alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, ct);
            if (alert is null || (alert.MachineId is int alertMachine && !await CanSeeMachineAsync(alertMachine, ct)))
                return CommandResult.Invalid("La alerta no existe o no tiene acceso a ella.");
            if (machineId is not null && machineId != alert.MachineId)
                return CommandResult.Invalid("La alerta es de otro equipo.");
            machineId = alert.MachineId;

            var existing = await db.Tickets.Where(t => t.AlertId == alertId && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Resolved)
                .Select(t => (int?)t.Id).FirstOrDefaultAsync(ct);
            if (existing is int open)
                return CommandResult.Conflict($"La alerta ya tiene el ticket #{open}.");
        }
        else if (machineId is int id && !await CanSeeMachineAsync(id, ct))
        {
            return CommandResult.Invalid("El equipo no existe o no tiene acceso a él.");
        }

        if (input.AsignadoA is int assignee)
        {
            if (!CanManage)
                return CommandResult.Invalid("No tiene permiso para asignar tickets.");
            if (!await IsTechnicianAsync(assignee, ct))
                return CommandResult.Invalid("Ese usuario no puede recibir tickets.");
        }

        var ticket = new Ticket
        {
            MachineId = machineId,
            AlertId = alert?.Id,
            CreatedById = user.UserId,
            AssignedToId = input.AsignadoA,
            Title = input.Titulo.Trim(),
            Description = Clean(input.Descripcion),
            Priority = EnumMemberConverter<TicketPriority>.FromProvider(input.Prioridad),
            Status = input.AsignadoA is null ? TicketStatus.Open : TicketStatus.InProgress,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("ticket.crear", "tickets", ticket.Id.ToString(), null, Snapshot(ticket), ct);

        // Abrir un ticket desde una alerta nueva es atenderla: pasa a "en proceso" (si quien lo crea gestiona alertas).
        if (alert is { Status: AlertStatus.New } && user.HasPermission(PermissionKeys.AlertsManage))
        {
            alert.Status = AlertStatus.InProgress;
            alert.HandledById = input.AsignadoA ?? user.UserId;
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync("alerta.atender", "alertas", alert.Id.ToString(), new { estado = "nueva" },
                new { estado = "en_proceso", nota = $"Ticket #{ticket.Id}" }, ct);
        }

        await NotifyAsync(ticket, ct);
        return CommandResult.Ok(ticket.Id);
    }

    public async Task<CommandResult> UpdateAsync(int id, TicketUpdateInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(id, ct);
        if (ticket is null)
            return CommandResult.NotFound();
        if (!CanManage && !(ticket.CreatedById == user.UserId && ticket.Status == TicketStatus.Open))
            return CommandResult.Invalid("Solo puede editar su ticket mientras siga abierto.");

        var before = Snapshot(ticket);
        ticket.Title = input.Titulo.Trim();
        ticket.Description = Clean(input.Descripcion);
        ticket.Priority = EnumMemberConverter<TicketPriority>.FromProvider(input.Prioridad);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("ticket.editar", "tickets", id.ToString(), before, Snapshot(ticket), ct);
        await NotifyAsync(ticket, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Mover en el tablero. Quien lo pasa a "en proceso" sin técnico asignado lo toma.</summary>
    public async Task<CommandResult> ChangeStatusAsync(int id, TicketStatusInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(id, ct);
        if (ticket is null)
            return CommandResult.NotFound();

        var status = EnumMemberConverter<TicketStatus>.FromProvider(input.Estado);
        if (ticket.Status == status)
            return CommandResult.Ok(id);

        var before = new { estado = Text(ticket.Status) };
        ticket.Status = status;
        ticket.ClosedAt = status is TicketStatus.Resolved or TicketStatus.Closed ? ticket.ClosedAt ?? Now : null;
        var takeIt = status == TicketStatus.InProgress && ticket.AssignedToId is null;
        if (takeIt)
            ticket.AssignedToId = user.UserId;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("ticket.estado", "tickets", id.ToString(), before, new { estado = input.Estado }, ct);
        if (takeIt)
            await audit.RecordAsync("ticket.asignar", "tickets", id.ToString(), new { asignado = (string?)null },
                new { asignado = await UserNameAsync(user.UserId, ct) }, ct);
        await NotifyAsync(ticket, ct);
        return CommandResult.Ok(id);
    }

    public async Task<CommandResult> AssignAsync(int id, TicketAssignInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(id, ct);
        if (ticket is null)
            return CommandResult.NotFound();
        if (input.UsuarioId is int assignee && !await IsTechnicianAsync(assignee, ct))
            return CommandResult.Invalid("Ese usuario no puede recibir tickets.");
        if (ticket.AssignedToId == input.UsuarioId)
            return CommandResult.Ok(id);

        var before = new { asignado = await UserNameAsync(ticket.AssignedToId, ct) };
        ticket.AssignedToId = input.UsuarioId;
        if (input.UsuarioId is not null && ticket.Status == TicketStatus.Open)
            ticket.Status = TicketStatus.InProgress;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("ticket.asignar", "tickets", id.ToString(), before, new { asignado = await UserNameAsync(input.UsuarioId, ct) }, ct);
        await NotifyAsync(ticket, ct);
        return CommandResult.Ok(id);
    }

    public async Task<CommandResult> CommentAsync(int id, TicketCommentInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(id, ct);
        if (ticket is null)
            return CommandResult.NotFound();
        if (ticket.Status == TicketStatus.Closed)
            return CommandResult.Conflict("El ticket está cerrado.");

        var comment = new TicketComment { TicketId = id, UserId = user.UserId, Text = input.Texto.Trim(), CreatedAt = Now };
        db.TicketComments.Add(comment);
        await db.SaveChangesAsync(ct);
        await NotifyAsync(ticket, ct);
        return CommandResult.Ok(comment.Id);
    }

    public async Task<CommandResult> AttachAsync(int id, TicketAttachmentInput input, CancellationToken ct)
    {
        var ticket = await FindAsync(id, ct);
        if (ticket is null)
            return CommandResult.NotFound();
        if (ticket.Status == TicketStatus.Closed)
            return CommandResult.Conflict("El ticket está cerrado.");
        if (await db.TicketAttachments.CountAsync(a => a.TicketId == id, ct) >= MaxAttachments)
            return CommandResult.Conflict($"Un ticket admite hasta {MaxAttachments} imágenes.");

        var base64 = input.Datos.Contains(',', StringComparison.Ordinal) ? input.Datos[(input.Datos.IndexOf(',') + 1)..] : input.Datos;
        byte[] content;
        try
        {
            content = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return CommandResult.Invalid("La imagen no es válida.");
        }
        if (content.Length == 0 || content.Length > TicketAttachmentInputValidator.MaxBytes)
            return CommandResult.Invalid("La imagen no puede pasar de 5 MB.");
        // El tipo declarado debe coincidir con el contenido: no se guardan archivos disfrazados de imagen.
        if (ImageExtension(input.Tipo, content) is not { } extension)
            return CommandResult.Invalid("El archivo no es una imagen JPG, PNG o WebP.");

        var path = await attachments.SaveAsync(user.CompanyId, id, extension, content, ct);
        var file = new TicketAttachment { TicketId = id, FileUrl = path, ContentType = input.Tipo, CreatedAt = Now };
        db.TicketAttachments.Add(file);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("ticket.adjuntar", "tickets", id.ToString(), null, new { adjunto = file.Id, tipo = input.Tipo, bytes = content.Length }, ct);
        await NotifyAsync(ticket, ct);
        return CommandResult.Ok(file.Id);
    }

    /// <summary>Extensión según las primeras bytes del archivo, o null si no coinciden con el tipo declarado.</summary>
    public static string? ImageExtension(string contentType, ReadOnlySpan<byte> content) => contentType switch
    {
        "image/jpeg" when content is [0xFF, 0xD8, 0xFF, ..] => "jpg",
        "image/png" when content is [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "png",
        "image/webp" when content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8) => "webp",
        _ => null,
    };

    private async Task NotifyAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.MachineId is null)
            return;
        var branchId = await db.Machines.Where(m => m.Id == ticket.MachineId).Select(m => (int?)m.BranchId).FirstOrDefaultAsync(ct);
        if (branchId is int branch)
            await notifier.TicketChangedAsync(user.CompanyId, branch, ticket.Id, ticket.MachineId, ct);
    }

    private static object Snapshot(Ticket t) => new
    {
        titulo = t.Title,
        prioridad = Text(t.Priority),
        estado = Text(t.Status),
        maquinaId = t.MachineId,
        alertaId = t.AlertId,
        asignadoA = t.AssignedToId,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

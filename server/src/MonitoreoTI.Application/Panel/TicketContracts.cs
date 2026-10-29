using FluentValidation;

namespace MonitoreoTI.Application.Panel;

// ------------------------------------------------------------------ tickets (W-24 a W-26)

/// <param name="Status">activos (por defecto: todo menos cerrado), abierto, en_proceso, en_espera, resuelto, cerrado o todos.</param>
/// <param name="Assignee">"yo", "sin" (sin asignar) o el id de un usuario.</param>
public sealed record TicketFilter(
    string? Query = null,
    string? Status = null,
    string? Priority = null,
    string? Assignee = null,
    int? BranchId = null,
    int? MachineId = null,
    long? AlertId = null,
    int Page = 1,
    int PageSize = 100);

public sealed record TicketCounts(int Open, int InProgress, int OnHold, int Resolved, int Closed, int Mine, int Unassigned);

public sealed record TicketListItem(
    int Id,
    string Title,
    string Priority,
    string Status,
    int? MachineId,
    string? MachineName,
    string? BranchName,
    long? AlertId,
    int? AssignedToId,
    string? AssignedTo,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    int CommentCount);

public sealed record TicketList(PagedResult<TicketListItem> Page, TicketCounts Counts);

public sealed record TicketCommentItem(int Id, string UserName, string Text, DateTime At);

public sealed record TicketAttachmentItem(int Id, string? ContentType, DateTime At);

public sealed record TicketHistoryItem(DateTime At, string Action, string? UserName, string? Detail);

public sealed record TicketDetail(
    int Id,
    string Title,
    string? Description,
    string Priority,
    string Status,
    int? MachineId,
    string? MachineName,
    string? MachineStatus,
    int? BranchId,
    string? BranchName,
    long? AlertId,
    string? AlertMessage,
    string? AlertStatus,
    int CreatedById,
    string CreatedBy,
    int? AssignedToId,
    string? AssignedTo,
    DateTime CreatedAt,
    DateTime? ClosedAt,
    IReadOnlyList<TicketCommentItem> Comments,
    IReadOnlyList<TicketAttachmentItem> Attachments,
    IReadOnlyList<TicketHistoryItem> History);

/// <summary>Imagen adjunta en base64 (la misma llamada sirve al panel y a la app móvil).</summary>
public sealed record TicketAttachmentContent(int Id, string ContentType, string Data);

public sealed record TechnicianItem(int Id, string Name);

public static class TicketValues
{
    public static readonly string[] Priorities = ["baja", "media", "alta", "urgente"];
    public static readonly string[] Statuses = ["abierto", "en_proceso", "en_espera", "resuelto", "cerrado"];
}

/// <summary>Ticket nuevo. Si viene de una alerta, el equipo sale de la alerta.</summary>
public sealed record TicketInput(string Titulo, string? Descripcion, string Prioridad, int? MaquinaId, long? AlertaId, int? AsignadoA);

public sealed class TicketInputValidator : AbstractValidator<TicketInput>
{
    public TicketInputValidator()
    {
        RuleFor(x => x.Titulo).NotEmpty().WithMessage("Escriba un título.").MaximumLength(200);
        RuleFor(x => x.Descripcion).MaximumLength(4000);
        RuleFor(x => x.Prioridad).Must(p => TicketValues.Priorities.Contains(p)).WithMessage("Prioridad no válida.");
    }
}

public sealed record TicketUpdateInput(string Titulo, string? Descripcion, string Prioridad);

public sealed class TicketUpdateInputValidator : AbstractValidator<TicketUpdateInput>
{
    public TicketUpdateInputValidator()
    {
        RuleFor(x => x.Titulo).NotEmpty().WithMessage("Escriba un título.").MaximumLength(200);
        RuleFor(x => x.Descripcion).MaximumLength(4000);
        RuleFor(x => x.Prioridad).Must(p => TicketValues.Priorities.Contains(p)).WithMessage("Prioridad no válida.");
    }
}

public sealed record TicketStatusInput(string Estado);

public sealed class TicketStatusInputValidator : AbstractValidator<TicketStatusInput>
{
    public TicketStatusInputValidator() =>
        RuleFor(x => x.Estado).Must(s => TicketValues.Statuses.Contains(s)).WithMessage("Estado no válido.");
}

public sealed record TicketAssignInput(int? UsuarioId);

public sealed record TicketCommentInput(string Texto);

public sealed class TicketCommentInputValidator : AbstractValidator<TicketCommentInput>
{
    public TicketCommentInputValidator() =>
        RuleFor(x => x.Texto).NotEmpty().WithMessage("Escriba el comentario.").MaximumLength(4000);
}

/// <summary>Foto o captura: data URI o base64 puro con su tipo.</summary>
public sealed record TicketAttachmentInput(string Tipo, string Datos);

public sealed class TicketAttachmentInputValidator : AbstractValidator<TicketAttachmentInput>
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public static readonly string[] Types = ["image/jpeg", "image/png", "image/webp"];

    public TicketAttachmentInputValidator()
    {
        RuleFor(x => x.Tipo).Must(t => Types.Contains(t)).WithMessage("Solo se aceptan imágenes JPG, PNG o WebP.");
        // 5 MB en base64 ocupan unos 6,7 millones de caracteres.
        RuleFor(x => x.Datos).NotEmpty().MaximumLength(MaxBytes / 3 * 4 + 200).WithMessage("La imagen no puede pasar de 5 MB.");
    }
}

/// <summary>Guarda y lee los adjuntos fuera de la base (carpeta del servidor).</summary>
public interface IAttachmentStore
{
    /// <summary>Guarda el archivo y devuelve su ruta relativa (lo que va en ticket_adjuntos.archivo_url).</summary>
    Task<string> SaveAsync(int companyId, int ticketId, string extension, byte[] content, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken);
}

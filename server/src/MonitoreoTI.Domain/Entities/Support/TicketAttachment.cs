using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Tabla: ticket_adjuntos.</summary>
public class TicketAttachment : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int TicketId { get; set; }
    public string FileUrl { get; set; } = string.Empty;

    /// <summary>Ej. 'image/jpeg'.</summary>
    public string? ContentType { get; set; }
    public DateTime CreatedAt { get; set; }
}

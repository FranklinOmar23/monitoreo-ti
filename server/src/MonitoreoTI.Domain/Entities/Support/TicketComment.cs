using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Tabla: ticket_comentarios.</summary>
public class TicketComment : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int TicketId { get; set; }
    public int UserId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

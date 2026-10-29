using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Tabla: tickets.</summary>
public class Ticket : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int? MachineId { get; set; }
    public int? DeviceId { get; set; }
    public long? AlertId { get; set; }
    public int CreatedById { get; set; }
    public int? AssignedToId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketStatus Status { get; set; } = TicketStatus.Open;
    public DateTime CreatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }

    public ICollection<TicketComment> Comments { get; set; } = new List<TicketComment>();
    public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
}

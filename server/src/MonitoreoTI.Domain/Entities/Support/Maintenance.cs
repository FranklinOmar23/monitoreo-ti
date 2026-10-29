using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Mantenimiento preventivo o correctivo. Tabla: mantenimientos.</summary>
public class Maintenance : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public int? TechnicianId { get; set; }
    public MaintenanceType Type { get; set; }
    public string? Description { get; set; }
    public DateOnly? ScheduledDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
}

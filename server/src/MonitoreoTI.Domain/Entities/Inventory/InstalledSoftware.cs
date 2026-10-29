using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Tabla: software_instalado.</summary>
public class InstalledSoftware : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Publisher { get; set; }
    public DateOnly? InstalledOn { get; set; }
}

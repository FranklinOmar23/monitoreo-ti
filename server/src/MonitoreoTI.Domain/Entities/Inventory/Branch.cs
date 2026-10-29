using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Sucursal de la empresa. Tabla: sucursales.</summary>
public class Branch : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Province { get; set; }
    public string? Address { get; set; }
    public int? ManagerId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

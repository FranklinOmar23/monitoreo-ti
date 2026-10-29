using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Tabla: departamentos.</summary>
public class Department : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
}

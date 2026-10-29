using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Componente de hardware (cpu, ram, disco, gpu, red, bateria...). Tabla: componentes.</summary>
public class Component : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Ej. '16 GB', '512 GB SSD'.</summary>
    public string? Capacity { get; set; }
    public HealthStatus HealthStatus { get; set; } = HealthStatus.Healthy;

    /// <summary>Dato de salud para mostrar, ej. "148 sectores dañados".</summary>
    public string? Detail { get; set; }
    public DateTime UpdatedAt { get; set; }
}

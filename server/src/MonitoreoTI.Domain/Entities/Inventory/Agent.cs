using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Agente instalado en una máquina. Tabla: agentes.</summary>
public class Agent : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Version { get; set; } = string.Empty;

    /// <summary>Hash de la credencial permanente del agente.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Consulta por SNMP los switches de su sucursal.</summary>
    public bool IsCollector { get; set; }
    public DateTime? LastContactAt { get; set; }
    public bool IsRevoked { get; set; }

    /// <summary>false = pendiente de aprobación (W-19): no puede conectarse hasta que lo aprueben.</summary>
    public bool IsApproved { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

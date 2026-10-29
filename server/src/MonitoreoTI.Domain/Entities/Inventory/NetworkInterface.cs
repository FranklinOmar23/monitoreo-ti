using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>
/// Tarjeta de red reportada por el agente; base del cruce por MAC con los switches.
/// Tabla: interfaces_red.
/// </summary>
public class NetworkInterface : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public NetworkInterfaceType Type { get; set; } = NetworkInterfaceType.Wired;
    public int? SpeedMbps { get; set; }
    public DateTime UpdatedAt { get; set; }
}

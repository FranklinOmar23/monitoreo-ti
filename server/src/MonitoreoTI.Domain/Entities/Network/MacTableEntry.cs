using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// MAC que un switch ve en un puerto; MachineId se llena al cruzar con NetworkInterface.MacAddress.
/// Tabla: tabla_mac.
/// </summary>
public class MacTableEntry : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int PortId { get; set; }
    public int? MachineId { get; set; }
    public string MacAddress { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public int? Vlan { get; set; }
    public string? Vendor { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Último switch y puerto de acceso donde se detectó cada máquina.
/// Vista de solo lectura: vw_ubicacion_red_maquina.
/// </summary>
public class MachineNetworkLocation
{
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public int? Vlan { get; set; }
    public string? BranchName { get; set; }
    public string? RoomName { get; set; }
    public string? RackName { get; set; }
    public int DeviceId { get; set; }
    public string SwitchName { get; set; } = string.Empty;
    public int PortId { get; set; }
    public int PortNumber { get; set; }
    public string? PortName { get; set; }
    public DateTime LastSeenAt { get; set; }
}

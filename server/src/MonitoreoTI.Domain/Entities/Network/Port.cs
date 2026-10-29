using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Puerto de un dispositivo de red. Tabla: puertos.</summary>
public class Port : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int DeviceId { get; set; }
    public int Number { get; set; }

    /// <summary>Ej. 'Gi1/0/12'.</summary>
    public string? Name { get; set; }
    public PortType Type { get; set; } = PortType.Rj45;
    public int? Vlan { get; set; }
    public int? SpeedMbps { get; set; }
    public DuplexMode? Duplex { get; set; }
    public PortStatus Status { get; set; } = PortStatus.Free;

    /// <summary>Uplink a otro equipo de red: se ignora en el cruce MAC.</summary>
    public bool IsTrunk { get; set; }

    /// <summary>Servidor con máquinas virtuales: muchas MAC sin ser troncal.</summary>
    public bool IsVmHost { get; set; }

    /// <summary>Un técnico fijó a mano lo conectado.</summary>
    public bool IsManualAssignment { get; set; }

    public NetworkDevice? Device { get; set; }
}

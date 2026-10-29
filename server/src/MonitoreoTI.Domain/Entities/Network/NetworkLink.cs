using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Cable entre equipos de red detectado por LLDP/CDP o registrado a mano. Tabla: enlaces_red.</summary>
public class NetworkLink : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int SourcePortId { get; set; }
    public int TargetPortId { get; set; }
    public LinkProtocol Protocol { get; set; } = LinkProtocol.Lldp;
    public DateTime DetectedAt { get; set; }
}

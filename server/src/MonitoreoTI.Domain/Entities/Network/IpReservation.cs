using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Rango de una subred que no se asigna (el DHCP, impresoras, cámaras). Tabla: reservas_ip.</summary>
public class IpReservation : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int SubnetId { get; set; }
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;

    /// <summary>manual (la cargó un técnico) o fortigate (rango y reservas del DHCP, se sincronizan solas).</summary>
    public string Origin { get; set; } = "manual";
    public DateTime CreatedAt { get; set; }
}

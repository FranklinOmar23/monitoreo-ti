using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Un período en que una dirección IP la tuvo un mismo equipo (misma MAC o mismo equipo con agente): desde la primera
/// vez que se vio hasta la última. Si deja de verse más de un día, o la tiene otro, empieza un período nuevo.
/// Tabla: historial_ip.
/// </summary>
public class IpHistoryEntry : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int SubnetId { get; set; }
    public string Ip { get; set; } = string.Empty;
    public string? MacAddress { get; set; }
    public string? Vendor { get; set; }
    public int? MachineId { get; set; }

    /// <summary>Nombre del equipo en ese momento: se conserva aunque luego lo renombren o lo den de baja.</summary>
    public string? MachineName { get; set; }

    public IpSource Source { get; set; } = IpSource.Arp;
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Subred IPv4 cuyo uso se inventaría (decisión 72): el recolector la barre con ping y la ARP dice qué MAC tiene cada
/// dirección. Tabla: subredes.
/// </summary>
public class Subnet : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Dirección de red ("192.168.3.0").</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary>22 a 30: hasta 1022 direcciones por barrido.</summary>
    public byte Prefix { get; set; }

    public int? Vlan { get; set; }
    public string? Gateway { get; set; }

    /// <summary>Agente que hace el barrido.</summary>
    public int? CollectorAgentId { get; set; }

    /// <summary>Equipo de red (el firewall o router de la VLAN) cuya tabla ARP se lee tras el barrido.</summary>
    public int? ArpDeviceId { get; set; }

    /// <summary>Cada cuántas horas se barre sola; 0 = solo a mano.</summary>
    public short SweepHours { get; set; } = 24;

    /// <summary>Una dirección que no se ve en estos días cuenta como libre.</summary>
    public short FreeAfterDays { get; set; } = 30;

    public DateTime? LastSweepAt { get; set; }
    public DateTime? SweepRequestedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}

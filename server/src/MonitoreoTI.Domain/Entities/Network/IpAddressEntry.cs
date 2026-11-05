using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Dirección de una subred que se vio en uso (por ping o por la ARP). Tabla: direcciones_ip.</summary>
public class IpAddressEntry : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int SubnetId { get; set; }
    public string Ip { get; set; } = string.Empty;
    public string? MacAddress { get; set; }
    public string? Vendor { get; set; }
    public int? MachineId { get; set; }
    public IpSource Source { get; set; } = IpSource.Arp;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>Última vez que respondió al ping (los equipos con firewall no responden aunque estén).</summary>
    public DateTime? LastPingAt { get; set; }

    /// <summary>La IP pasó de esta MAC a la actual: equipo reemplazado o dos equipos con la misma IP.</summary>
    public string? PreviousMacAddress { get; set; }

    public DateTime? MacChangedAt { get; set; }

    /// <summary>Nombre del equipo que respondió en esa IP (NetBIOS de Windows o DNS inverso).</summary>
    public string? Hostname { get; set; }

    /// <summary>netbios o dns.</summary>
    public string? HostnameSource { get; set; }

    /// <summary>Interfaz del firewall o router donde se vio en la ARP ("VLAN3-x3", "wifi-invitados").</summary>
    public string? Interface { get; set; }

    /// <summary>Sistema operativo según el FortiGate ("Windows 10", "Android").</summary>
    public string? OperatingSystem { get; set; }

    /// <summary>true: la IP la dio el DHCP del FortiGate (concesión vigente); null: no se sabe.</summary>
    public bool? Dhcp { get; set; }

    public DateTime? DhcpExpiresAt { get; set; }
}

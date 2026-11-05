using FluentValidation;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Application.Network;

// ------------------------------------------------------------------ panel (decisión 72)

/// <param name="Red">Notación CIDR: "192.168.3.0/24" (de /22 a /30).</param>
/// <param name="BarridoHoras">Cada cuántas horas se barre sola; 0 = solo con "Escanear ahora".</param>
/// <param name="DiasLibre">Una dirección que no se ve en estos días cuenta como libre.</param>
public sealed record SubnetInput(
    string Nombre, int SucursalId, string Red, int? Vlan, string? PuertaEnlace, int? RecolectorId, int? DispositivoArpId,
    int BarridoHoras = 24, int DiasLibre = 30);

public sealed record IpReservationInput(string Desde, string Hasta, string Motivo);

/// <param name="CollectorCanSweep">El agente del recolector sabe barrer (0.3.4 o posterior); si no, la subred se llena solo con la ARP.</param>
/// <param name="Used">En uso (conocidas y sin identificar).</param>
/// <param name="Recent">Vistas fuera de las 24 h pero dentro de los días de la subred.</param>
public sealed record SubnetItem(
    int Id, string Name, int BranchId, string BranchName, string Cidr, int? Vlan, string? Gateway,
    int? CollectorId, string? CollectorName, bool CollectorOnline, bool CollectorCanSweep, int? ArpDeviceId, string? ArpDeviceName,
    int SweepHours, int FreeAfterDays, DateTime? LastSweepAt, DateTime? SweepRequestedAt, string? LastError,
    int Total, int Used, int Unidentified, int Recent, int Reserved, int Free);

/// <param name="State">en_uso, sin_identificar, reciente, reservada o libre.</param>
/// <param name="MachineSource">agente (lo reporta el equipo) o mac (la MAC de la ARP es de un equipo conocido).</param>
/// <param name="Port">Switch y puerto donde se ve su MAC ("SW-SD-01 Gi1/0/12").</param>
/// <param name="MacChanged">La IP cambió de MAC hace poco: equipo reemplazado o dos equipos con la misma IP.</param>
public sealed record IpItem(
    string Ip, string State, string? Mac, string? Vendor, int? MachineId, string? MachineName, string? MachineSource,
    string? Port, DateTime? FirstSeenAt, DateTime? LastSeenAt, DateTime? LastPingAt, string? Reservation,
    bool IsGateway, bool MacChanged, string? PreviousMac,
    /// <summary>Nombre del equipo (NetBIOS o DNS inverso) y de dónde salió; interfaz o VLAN donde lo ve el firewall.</summary>
    string? Hostname = null, string? HostnameSource = null, string? Interface = null,
    /// <summary>Según el FortiGate: sistema operativo, y "dhcp" (concesión hasta DhcpExpiresAt) o "fija" (la API está activa y no la dio el DHCP).</summary>
    string? OperatingSystem = null, string? Assignment = null, DateTime? DhcpExpiresAt = null);

/// <summary>Un período del historial de una IP: quién la tuvo, desde cuándo y hasta cuándo.</summary>
/// <param name="Source">arp, ping o agente: cómo se supo la primera vez.</param>
/// <param name="Current">Todavía la tiene (se vio en las últimas 24 h).</param>
public sealed record IpHistoryItem(
    string? Mac, string? Vendor, int? MachineId, string? MachineName, string Source, DateTime From, DateTime To, bool Current);

/// <param name="Origin">manual o fortigate (del DHCP del FortiGate: no se borra a mano, se sincroniza).</param>
public sealed record IpReservationItem(int Id, string From, string To, string Reason, int Count, string Origin = "manual");

public sealed record SubnetDetail(SubnetItem Subnet, IReadOnlyList<IpItem> Addresses, IReadOnlyList<IpReservationItem> Reservations, string? Suggested);

public sealed class SubnetInputValidator : AbstractValidator<SubnetInput>
{
    public SubnetInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre de la subred.").MaximumLength(100);
        RuleFor(x => x.SucursalId).GreaterThan(0).WithMessage("Elija la sucursal.");
        RuleFor(x => x.Red).Must(r => IpRules.TryParseCidr(r, out _, out _))
            .WithMessage($"Escriba la red como 192.168.3.0/24 (de /{IpRules.MinPrefix} a /{IpRules.MaxPrefix}).");
        RuleFor(x => x.Vlan).InclusiveBetween(1, 4094).When(x => x.Vlan is not null).WithMessage("La VLAN va de 1 a 4094.");
        RuleFor(x => x.PuertaEnlace).Must(g => IpRules.TryParse(g, out _)).When(x => !string.IsNullOrWhiteSpace(x.PuertaEnlace))
            .WithMessage("La puerta de enlace no es una IP válida.");
        RuleFor(x => x.BarridoHoras).InclusiveBetween(0, 168).WithMessage("El barrido va de 0 (solo a mano) a 168 horas.");
        RuleFor(x => x.DiasLibre).InclusiveBetween(1, 365).WithMessage("Los días para considerar libre van de 1 a 365.");
    }
}

public sealed class IpReservationInputValidator : AbstractValidator<IpReservationInput>
{
    public IpReservationInputValidator()
    {
        RuleFor(x => x.Desde).Must(v => IpRules.TryParse(v, out _)).WithMessage("La IP inicial no es válida.");
        RuleFor(x => x.Hasta).Must(v => IpRules.TryParse(v, out _)).WithMessage("La IP final no es válida.");
        RuleFor(x => x.Motivo).NotEmpty().WithMessage("Escriba el motivo de la reserva.").MaximumLength(150);
    }
}

// ------------------------------------------------------------------ servidor ↔ recolector

/// <summary>Lo que el servidor pide al recolector ("barrido_ip"): las direcciones a probar y de qué equipo leer la ARP.</summary>
public sealed record IpSweepRequest(int SubredId, IReadOnlyList<string> Ips, int? ArpDispositivoId);

/// <summary>
/// Lo que devuelve el recolector ("barrido_ip_resultado"): las IP que respondieron al ping y las parejas IP-MAC de la
/// ARP (la del propio recolector y la del equipo de red indicado).
/// </summary>
public sealed record BarridoIpData(
    int SubredId, bool Ok, string? Error, IReadOnlyList<string>? Responden, IReadOnlyList<SnmpArpData>? Arp,
    /// <summary>Nombres de los equipos que se vieron (agente 0.3.5 en adelante).</summary>
    IReadOnlyList<IpNameData>? Nombres = null);

/// <param name="Origen">netbios (el nombre que da el propio Windows) o dns (registro inverso).</param>
public sealed record IpNameData(string Ip, string Nombre, string Origen);

/// <summary>Manda un barrido a un recolector conectado; false si no lo está.</summary>
public interface IIpSweepDispatcher
{
    Task<bool> SendAsync(int companyId, int agentId, IpSweepRequest request, CancellationToken cancellationToken);
}

/// <summary>Lo que el gateway de agentes usa: guardar un barrido y saber qué barridos le tocan a un recolector.</summary>
public interface IIpSweeps
{
    Task SaveResultAsync(AgentIdentity identity, BarridoIpData data, DateTime receivedAtUtc, CancellationToken cancellationToken);

    /// <summary>Barridos vencidos de ese recolector; los marca como pedidos.</summary>
    Task<IReadOnlyList<IpSweepRequest>> DueAsync(AgentIdentity identity, DateTime nowUtc, CancellationToken cancellationToken);
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Network;

public static class IpReservationOrigins
{
    public const string Manual = "manual";
    public const string FortiGate = "fortigate";
}

/// <summary>
/// Guarda lo que el recolector leyó de la API del FortiGate (decisión 76):
/// <list type="bullet">
/// <item>los dispositivos en línea van al inventario de IP con el nombre y el sistema que da el FortiGate, y si su IP
/// es una concesión del DHCP (y hasta cuándo) o fija;</item>
/// <item>las concesiones de equipos que no están en línea solo actualizan el nombre y el DHCP de lo ya conocido: una
/// concesión vigente no prueba que el equipo esté conectado;</item>
/// <item>el rango y las direcciones reservadas del DHCP quedan como reservas de origen "fortigate" de cada subred,
/// para que la sugerencia de IP libre no ofrezca una que el DHCP puede repartir.</item>
/// </list>
/// </summary>
public sealed class FortiGateData(IServiceScopeFactory scopes, OuiDirectory oui, ILogger<FortiGateData> logger) : IFortiGateData
{
    public async Task SaveAsync(AgentIdentity identity, FortiGateDatosData data, DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(identity.CompanyId);
        var db = scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();

        var device = await db.NetworkDevices.FirstOrDefaultAsync(
            d => d.Id == data.DispositivoId && d.CollectorAgentId == identity.AgentId && d.ApiUrl != null, cancellationToken);
        if (device is null)
        {
            logger.LogWarning("Agente {AgentId}: datos de API del dispositivo {DeviceId}, que no tiene asignado; se ignoran.",
                identity.AgentId, data.DispositivoId);
            return;
        }
        device.ApiLastError = Cut(data.Error, 300);
        if (!data.Ok)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        device.ApiLastOkAt = receivedAtUtc;

        var leases = Leases(data.Dhcp);
        var online = Observations(data, leases);
        await IpInventory.RecordAsync(db, oui, online, null, receivedAtUtc, cancellationToken);
        if (leases is not null)
            await UpdateOfflineLeasesAsync(db, leases, online.Select(o => o.Ip).ToHashSet(), cancellationToken);
        if (data.ServidoresDhcp is not null)
            await SyncReservationsAsync(db, data.ServidoresDhcp, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed record Lease(uint Ip, string? Mac, string? Name, DateTime? ExpiresAt);

    /// <summary>Concesiones válidas por IP; null si no se pudieron leer.</summary>
    private static Dictionary<uint, Lease>? Leases(IReadOnlyList<FortiLeaseData>? dhcp)
    {
        if (dhcp is null)
            return null;
        var result = new Dictionary<uint, Lease>();
        foreach (var l in dhcp)
        {
            if (!IpRules.TryParse(l.Ip, out var ip))
                continue;
            DateTime? expires = l.Vence is long s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
            result[ip] = new Lease(ip, Mac(l.Mac), Name(l.Nombre), expires);
        }
        return result;
    }

    /// <summary>
    /// Lo que se da por visto ahora: los dispositivos en línea, con los datos de su concesión. Si el FortiGate no dio
    /// la lista de dispositivos (permiso o versión), cuentan las concesiones.
    /// </summary>
    private static List<IpInventory.Observation> Observations(FortiGateDatosData data, Dictionary<uint, Lease>? leases)
    {
        var list = new List<IpInventory.Observation>();
        if (data.Dispositivos is null)
        {
            foreach (var l in leases?.Values ?? Enumerable.Empty<Lease>())
                list.Add(new IpInventory.Observation(l.Ip, l.Mac, false,
                    Hostname: l.Name, HostnameSource: l.Name is null ? null : "fortigate", Dhcp: true, DhcpExpiresAt: l.ExpiresAt));
            return list;
        }
        foreach (var d in data.Dispositivos)
        {
            if (!IpRules.TryParse(d.Ip, out var ip))
                continue;
            var lease = leases?.GetValueOrDefault(ip);
            var mac = Mac(d.Mac) ?? lease?.Mac;
            var name = Name(d.Nombre) ?? lease?.Name;
            list.Add(new IpInventory.Observation(ip, mac, false,
                Hostname: name, HostnameSource: name is null ? null : "fortigate",
                OperatingSystem: Name(d.Sistema),
                // Sin la lista del DHCP no se sabe si es fija.
                Dhcp: leases is null ? null : lease is not null,
                DhcpExpiresAt: lease?.ExpiresAt));
        }
        return list;
    }

    private static async Task UpdateOfflineLeasesAsync(MonitoreoDbContext db, Dictionary<uint, Lease> leases, HashSet<uint> online, CancellationToken ct)
    {
        var offline = leases.Values.Where(l => !online.Contains(l.Ip)).ToDictionary(l => IpRules.ToText(l.Ip));
        if (offline.Count == 0)
            return;
        var ips = offline.Keys.ToList();
        foreach (var row in await db.IpAddresses.Where(r => ips.Contains(r.Ip)).ToListAsync(ct))
        {
            var lease = offline[row.Ip];
            // La concesión es de otro equipo si la MAC no coincide.
            if (lease.Mac is not null && row.MacAddress is not null && lease.Mac != row.MacAddress)
                continue;
            row.Dhcp = true;
            row.DhcpExpiresAt = lease.ExpiresAt;
            if (lease.Name is not null)
            {
                row.Hostname = lease.Name;
                row.HostnameSource = "fortigate";
            }
        }
    }

    /// <summary>Reemplaza las reservas "fortigate" por el rango y las reservadas que el DHCP tiene hoy.</summary>
    private static async Task SyncReservationsAsync(MonitoreoDbContext db, IReadOnlyList<FortiDhcpServerData> servers, CancellationToken ct)
    {
        var subnets = (await db.Subnets.AsNoTracking().Select(s => new { s.Id, s.Network, s.Prefix }).ToListAsync(ct))
            .Select(s => (s.Id, Network: IpRules.TryParse(s.Network, out var n) ? n : 0u, Prefix: (int)s.Prefix))
            .Where(s => s.Network != 0)
            .ToList();
        int? SubnetOf(uint from, uint to) =>
            subnets.Where(s => IpRules.IsValidRange(s.Network, s.Prefix, from, to)).Select(s => (int?)s.Id).FirstOrDefault();

        var wanted = new Dictionary<(int Subnet, string From, string To), string>();
        foreach (var server in servers)
        {
            var where = string.IsNullOrWhiteSpace(server.Interfaz) ? "" : $" ({server.Interfaz.Trim()})";
            foreach (var range in server.Rangos ?? [])
                if (IpRules.TryParse(range.Desde, out var from) && IpRules.TryParse(range.Hasta, out var to) && SubnetOf(from, to) is int subnet)
                    wanted.TryAdd((subnet, IpRules.ToText(from), IpRules.ToText(to)), Cut($"Rango del DHCP del FortiGate{where}", 150)!);
            foreach (var reserved in server.Reservadas ?? [])
                if (IpRules.TryParse(reserved.Ip, out var ip) && SubnetOf(ip, ip) is int subnet)
                {
                    var who = Name(reserved.Descripcion) ?? Mac(reserved.Mac);
                    var text = IpRules.ToText(ip);
                    // Una IP reservada dentro del rango queda con su propio motivo.
                    wanted[(subnet, text, text)] = Cut($"Reservada en el DHCP del FortiGate{(who is null ? "" : $": {who}")}", 150)!;
                }
        }

        var current = await db.IpReservations.Where(r => r.Origin == IpReservationOrigins.FortiGate).ToListAsync(ct);
        foreach (var r in current)
        {
            if (wanted.Remove((r.SubnetId, r.From, r.To), out var reason))
                r.Reason = reason;
            else
                db.IpReservations.Remove(r);
        }
        foreach (var ((subnet, from, to), reason) in wanted)
            db.IpReservations.Add(new IpReservation
            {
                SubnetId = subnet,
                From = from,
                To = to,
                Reason = reason,
                Origin = IpReservationOrigins.FortiGate,
            });
    }

    private static string? Mac(string? value) =>
        value is not null && MacAddress.TryNormalize(value, out var mac) && IpRules.IsUnicastMac(mac) ? mac : null;

    private static string? Name(string? value) => string.IsNullOrWhiteSpace(value) ? null : Cut(value.Trim(), 255);

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

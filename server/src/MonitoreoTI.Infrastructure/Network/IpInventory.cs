using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Inventario de direcciones IP (decisión 72): guarda lo que se vio en uso, por ping o por la ARP de un equipo de red
/// o del recolector, en la subred registrada que lo contiene. Lo que cae fuera de toda subred se ignora.
/// </summary>
public static class IpInventory
{
    /// <summary>
    /// Una dirección vista: con su MAC si vino de la ARP; <paramref name="Pinged"/> si respondió al ping.
    /// <paramref name="FromAgent"/>: la reporta el agente del equipo <paramref name="MachineId"/> (solo va al historial:
    /// el estado de la dirección ya lo toma de sus tarjetas de red).
    /// </summary>
    public sealed record Observation(
        uint Ip, string? Mac, bool Pinged, int? MachineId = null, bool FromAgent = false,
        string? Hostname = null, string? HostnameSource = null, string? Interface = null,
        string? OperatingSystem = null, bool? Dhcp = null, DateTime? DhcpExpiresAt = null);

    /// <summary>Un equipo que deja de verse más que esto y vuelve abre un período nuevo en el historial.</summary>
    public static readonly TimeSpan HistoryGap = TimeSpan.FromHours(24);

    /// <summary>Lo que se guarda del historial.</summary>
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);

    /// <summary>Lo que el gateway manda al recolector en "barrido_ip".</summary>
    public static object Message(IpSweepRequest request) => new
    {
        subred_id = request.SubredId,
        ips = request.Ips,
        arp_dispositivo_id = request.ArpDispositivoId,
    };

    /// <summary>El barrido de una subred: todas sus direcciones; la ARP solo si ese equipo lo consulta el mismo recolector.</summary>
    public static async Task<IpSweepRequest?> RequestAsync(MonitoreoDbContext db, Subnet subnet, CancellationToken ct)
    {
        if (subnet.CollectorAgentId is not int agentId || !IpRules.TryParse(subnet.Network, out var network))
            return null;
        int? arp = subnet.ArpDeviceId is int deviceId
            && await db.NetworkDevices.AnyAsync(d => d.Id == deviceId && d.CollectorAgentId == agentId && d.Status != NetworkDeviceStatus.Retired, ct)
            ? deviceId : null;
        return new IpSweepRequest(subnet.Id, IpRules.Hosts(network, subnet.Prefix).Select(IpRules.ToText).ToList(), arp);
    }

    /// <summary>
    /// Convierte la ARP de un sondeo (IP y MAC en texto) en observaciones; descarta lo inválido y la difusión.
    /// <paramref name="interfaceName"/>: el nombre de la interfaz del equipo por su índice (VLAN3-x3), si se conoce.
    /// </summary>
    public static List<Observation> FromArp(IEnumerable<SnmpArpData> arp, Func<int, string?>? interfaceName = null)
    {
        var list = new List<Observation>();
        foreach (var entry in arp)
            if (IpRules.TryParse(entry.Ip, out var ip) && MacAddress.TryNormalize(entry.Mac, out var mac) && IpRules.IsUnicastMac(mac))
                list.Add(new Observation(ip, mac, false,
                    Interface: entry.IfIndex is int index && interfaceName?.Invoke(index) is { Length: > 0 } name ? Cut(name, 64) : null));
        return list;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Guarda las observaciones (sin SaveChanges: lo hace quien llama). <paramref name="onlySubnetId"/> limita a una
    /// subred (los pings de un barrido); sin él, cada dirección va a la subred de la empresa que la contenga.
    /// </summary>
    public static async Task RecordAsync(
        MonitoreoDbContext db, OuiDirectory oui, IReadOnlyCollection<Observation> observations, int? onlySubnetId, DateTime now, CancellationToken ct)
    {
        if (observations.Count == 0)
            return;
        var subnets = (await db.Subnets.AsNoTracking()
                .Where(s => onlySubnetId == null || s.Id == onlySubnetId)
                .Select(s => new { s.Id, s.Network, s.Prefix })
                .ToListAsync(ct))
            .Select(s => (s.Id, Network: IpRules.TryParse(s.Network, out var n) ? n : 0u, (int)s.Prefix))
            .Where(s => s.Network != 0)
            .ToList();
        if (subnets.Count == 0)
            return;

        // Una dirección puede venir varias veces (ping y ARP): se juntan.
        var merged = new Dictionary<(int Subnet, uint Ip), Observation>();
        foreach (var o in observations)
        {
            var subnet = subnets.FirstOrDefault(s => IpRules.Contains(s.Network, s.Item3, o.Ip));
            if (subnet.Id == 0)
                continue;
            var key = (subnet.Id, o.Ip);
            merged[key] = merged.TryGetValue(key, out var previous)
                ? previous with
                {
                    Mac = o.Mac ?? previous.Mac,
                    Pinged = o.Pinged || previous.Pinged,
                    MachineId = o.MachineId ?? previous.MachineId,
                    Hostname = o.Hostname ?? previous.Hostname,
                    HostnameSource = o.Hostname is null ? previous.HostnameSource : o.HostnameSource,
                    Interface = o.Interface ?? previous.Interface,
                    OperatingSystem = o.OperatingSystem ?? previous.OperatingSystem,
                    Dhcp = o.Dhcp ?? previous.Dhcp,
                    DhcpExpiresAt = o.Dhcp is null ? previous.DhcpExpiresAt : o.DhcpExpiresAt,
                }
                : o;
        }
        if (merged.Count == 0)
            return;

        var macs = merged.Values.Select(v => v.Mac).Where(m => m is not null).Cast<string>().Distinct().ToList();
        var machineByMac = await MachinesByMacAsync(db, macs, ct);
        var machineIds = merged.Values.Select(v => v.MachineId ?? (v.Mac is null ? null : machineByMac.GetValueOrDefault(v.Mac)))
            .OfType<int>().Distinct().ToList();
        var names = await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        foreach (var group in merged.GroupBy(m => m.Key.Subnet))
        {
            var ips = group.Select(g => IpRules.ToText(g.Key.Ip)).ToList();
            var rows = await db.IpAddresses.Where(r => r.SubnetId == group.Key && ips.Contains(r.Ip)).ToDictionaryAsync(r => r.Ip, ct);
            var latest = (await db.IpHistory.Where(h => h.SubnetId == group.Key && ips.Contains(h.Ip))
                    .GroupBy(h => h.Ip)
                    .Select(g => g.OrderByDescending(h => h.To).First())
                    .ToListAsync(ct))
                .ToDictionary(h => h.Ip);

            foreach (var (key, o) in group)
            {
                var ip = IpRules.ToText(key.Ip);
                var mac = o.Mac;
                var machineId = o.MachineId ?? (mac is null ? null : machineByMac.GetValueOrDefault(mac));
                // Sin equipo conocido, el historial guarda el nombre que dio en la red (NetBIOS o DNS).
                RecordHistory(db, latest, group.Key, ip, mac, machineId, machineId is int id ? names.GetValueOrDefault(id) : o.Hostname,
                    mac is null ? null : oui.Vendor(mac), o.FromAgent ? IpSource.Agent : mac is null ? IpSource.Ping : IpSource.Arp, now);

                if (o.FromAgent)
                    continue;
                if (!rows.TryGetValue(ip, out var row))
                {
                    row = new IpAddressEntry { SubnetId = group.Key, Ip = ip, FirstSeenAt = now };
                    db.IpAddresses.Add(row);
                    rows[ip] = row;
                }
                row.LastSeenAt = now;
                row.Source = mac is null ? IpSource.Ping : IpSource.Arp;
                if (o.Pinged)
                    row.LastPingAt = now;
                if (o.Hostname is not null)
                {
                    row.Hostname = Cut(o.Hostname, 255);
                    row.HostnameSource = o.HostnameSource;
                }
                if (o.Interface is not null)
                    row.Interface = o.Interface;
                if (o.OperatingSystem is not null)
                    row.OperatingSystem = Cut(o.OperatingSystem, 100);
                if (o.Dhcp is bool dhcp)
                {
                    row.Dhcp = dhcp;
                    row.DhcpExpiresAt = o.DhcpExpiresAt;
                }
                if (mac is null)
                    continue;
                if (row.MacAddress is not null && row.MacAddress != mac)
                {
                    row.PreviousMacAddress = row.MacAddress;
                    row.MacChangedAt = now;
                }
                if (row.MacAddress != mac)
                    row.Vendor = oui.Vendor(mac);
                row.MacAddress = mac;
                row.MachineId = machineByMac.GetValueOrDefault(mac);
            }

            var old = now - HistoryRetention;
            await db.IpHistory.Where(h => h.SubnetId == group.Key && h.To < old).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>
    /// Alarga el período vigente si es el mismo equipo y se vio hace poco; si no, abre uno nuevo. Un ping no dice
    /// quién es: alarga el período de quien la tenía.
    /// </summary>
    private static void RecordHistory(
        MonitoreoDbContext db, Dictionary<string, IpHistoryEntry> latest, int subnetId, string ip, string? mac, int? machineId,
        string? machineName, string? vendor, IpSource source, DateTime now)
    {
        if (latest.TryGetValue(ip, out var current) && current.To >= now - HistoryGap)
        {
            var same = mac is not null
                ? current.MacAddress is null ? current.MachineId is null || current.MachineId == machineId : current.MacAddress == mac
                : machineId is null || current.MachineId is null || current.MachineId == machineId;
            if (same)
            {
                current.To = now;
                current.MacAddress ??= mac;
                current.Vendor ??= vendor;
                current.MachineId ??= machineId;
                current.MachineName ??= machineName;
                return;
            }
        }
        var entry = new IpHistoryEntry
        {
            SubnetId = subnetId,
            Ip = ip,
            MacAddress = mac,
            Vendor = vendor,
            MachineId = machineId,
            MachineName = machineName,
            Source = source,
            From = now,
            To = now,
        };
        db.IpHistory.Add(entry);
        latest[ip] = entry;
    }

    /// <summary>Equipos por MAC: las tarjetas que reportan los agentes y la MAC de la ficha de los equipos sin agente.</summary>
    private static async Task<Dictionary<string, int?>> MachinesByMacAsync(MonitoreoDbContext db, List<string> macs, CancellationToken ct)
    {
        var result = new Dictionary<string, int?>();
        if (macs.Count == 0)
            return result;
        foreach (var card in await db.NetworkInterfaces.AsNoTracking().Where(i => macs.Contains(i.MacAddress))
                     .Select(i => new { i.MacAddress, i.MachineId }).ToListAsync(ct))
            result[card.MacAddress] = card.MachineId;
        var withoutAgent = await db.Machines.AsNoTracking()
            .Where(m => m.MacAddress != null && m.Status != MachineStatus.Retired && m.Agent == null)
            .Select(m => new { m.Id, m.MacAddress })
            .ToListAsync(ct);
        foreach (var m in withoutAgent)
            if (MacAddress.TryNormalize(m.MacAddress!, out var mac) && macs.Contains(mac))
                result.TryAdd(mac, m.Id);
        return result;
    }
}

/// <summary>
/// El lado del gateway: qué barridos le tocan a un recolector conectado y qué hacer con su resultado. Singleton: cada
/// operación abre su ámbito con la empresa del agente.
/// </summary>
public sealed class IpSweeps(IServiceScopeFactory scopes, OuiDirectory oui, ILogger<IpSweeps> logger) : IIpSweeps
{
    /// <summary>Un barrido pedido que no vuelve en este tiempo se puede pedir otra vez.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    public async Task SaveResultAsync(AgentIdentity identity, BarridoIpData data, DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var subnet = await db.Subnets.FirstOrDefaultAsync(s => s.Id == data.SubredId && s.CollectorAgentId == identity.AgentId, cancellationToken);
        if (subnet is null || !IpRules.TryParse(subnet.Network, out var network))
        {
            logger.LogWarning("Agente {AgentId}: barrido de la subred {SubnetId}, que no tiene asignada; se ignora.", identity.AgentId, data.SubredId);
            return;
        }

        subnet.SweepRequestedAt = null;
        if (!data.Ok)
        {
            subnet.LastError = Cut(data.Error ?? "El barrido no se pudo hacer.", 300);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // Los pings cuentan solo dentro de esta subred; la ARP (del firewall, que ve todas las VLAN) va a la que corresponda.
        var named = new Dictionary<uint, IpNameData>();
        foreach (var n in data.Nombres ?? [])
            if (IpRules.TryParse(n.Ip, out var ip) && !string.IsNullOrWhiteSpace(n.Nombre) && n.Origen is "netbios" or "dns")
                named[ip] = n;
        IpInventory.Observation Named(IpInventory.Observation o) =>
            named.TryGetValue(o.Ip, out var n) ? o with { Hostname = n.Nombre.Trim(), HostnameSource = n.Origen } : o;
        var pings = (data.Responden ?? [])
            .Select(text => IpRules.TryParse(text, out var ip) ? ip : 0u)
            .Where(ip => ip != 0 && IpRules.Contains(network, subnet.Prefix, ip))
            .Select(ip => Named(new IpInventory.Observation(ip, null, true)))
            .ToList();
        // Una sola pasada: dos guardarían la misma dirección dos veces (lo agregado aún no está en la base).
        await IpInventory.RecordAsync(db, oui, [.. pings, .. IpInventory.FromArp(data.Arp ?? []).Select(Named)], null, receivedAtUtc, cancellationToken);
        subnet.LastSweepAt = receivedAtUtc;
        subnet.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<IpSweepRequest>> DueAsync(AgentIdentity identity, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        // Un agente anterior a 0.3.4 no sabe barrer: no se le pide (la subred se llena con la ARP de los sondeos).
        var version = await db.Agents.AsNoTracking().Where(a => a.Id == identity.AgentId).Select(a => a.Version).FirstOrDefaultAsync(cancellationToken);
        if (!IpRules.SupportsSweep(version))
            return [];
        var subnets = await db.Subnets.Where(s => s.CollectorAgentId == identity.AgentId && s.SweepHours > 0).ToListAsync(cancellationToken);
        var due = subnets.Where(s =>
            (s.LastSweepAt is null || s.LastSweepAt <= nowUtc.AddHours(-s.SweepHours))
            && (s.SweepRequestedAt is null || s.SweepRequestedAt <= nowUtc - RequestTimeout)).ToList();

        var requests = new List<IpSweepRequest>();
        foreach (var subnet in due)
        {
            if (await IpInventory.RequestAsync(db, subnet, cancellationToken) is not { } request)
                continue;
            subnet.SweepRequestedAt = nowUtc;
            requests.Add(request);
        }
        await db.SaveChangesAsync(cancellationToken);
        return requests;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private static MonitoreoDbContext Open(AsyncServiceScope scope, int companyId)
    {
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        return scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
    }
}

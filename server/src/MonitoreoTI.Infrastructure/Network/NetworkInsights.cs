using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Lo que sale del recolector y la correlación (W-42, W-49, W-52, W-53, W-66 y la conexión de red de la ficha).
/// Todo se limita a los dispositivos de las sucursales que ve el usuario.
/// </summary>
public sealed class NetworkInsights(MonitoreoDbContext db, NetworkQueries queries, TimeProvider clock)
{
    /// <summary>Un agente con contacto en este tiempo cuenta como conectado.</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);

    /// <summary>Lo visto en este tiempo cuenta como actual (equipos desconocidos, diferencias).</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed record PortInfo(int Id, int DeviceId, string DeviceName, DeviceType DeviceType, bool IsActive, string Label, bool IsTrunk, bool IsManual, string PortName);

    private async Task<Dictionary<int, PortInfo>> VisiblePortsAsync(CancellationToken ct)
    {
        var devices = (await queries.VisibleDevicesAsync(ct)).Select(d => d.Id);
        return await db.Ports.AsNoTracking().Where(p => devices.Contains(p.DeviceId))
            .Select(p => new PortInfo(p.Id, p.DeviceId, p.Device!.Name, p.Device.Type, p.Device.Model!.IsActiveDevice,
                p.Device.Name + " " + (p.Name ?? p.Number.ToString()), p.IsTrunk, p.IsManualAssignment, p.Name ?? p.Number.ToString()))
            .ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>Puertos que llegan a un access point (es un AP, o LLDP ve un AP del otro lado): lo que se ve ahí es Wi-Fi.</summary>
    private async Task<HashSet<int>> AccessPointPortsAsync(Dictionary<int, PortInfo> ports, CancellationToken ct)
    {
        var result = ports.Values.Where(p => p.DeviceType == DeviceType.AccessPoint).Select(p => p.Id).ToHashSet();
        var apPorts = ports.Values.Where(p => p.DeviceType == DeviceType.AccessPoint).Select(p => p.Id).ToList();
        var links = await db.NetworkLinks.AsNoTracking()
            .Where(l => apPorts.Contains(l.SourcePortId) || apPorts.Contains(l.TargetPortId))
            .Select(l => new { l.SourcePortId, l.TargetPortId }).ToListAsync(ct);
        foreach (var l in links)
        {
            result.Add(l.SourcePortId);
            result.Add(l.TargetPortId);
        }
        return result;
    }

    // ------------------------------------------------------------------ recolectores (W-49)

    public async Task<IReadOnlyList<CollectorItem>> CollectorsAsync(int? branchId, CancellationToken ct)
    {
        var allowed = await queries.AllowedBranchesAsync(ct);
        var online = Now - OnlineWindow;
        var machines = db.Machines.AsNoTracking().Where(m => m.Agent != null && !m.Agent.IsRevoked && m.Agent.IsApproved && m.Status != MachineStatus.Retired);
        if (allowed is not null)
            machines = machines.Where(m => allowed.Contains(m.BranchId));
        if (branchId is int branch)
            machines = machines.Where(m => m.BranchId == branch);
        return await machines
            .OrderByDescending(m => m.Agent!.IsCollector).ThenBy(m => m.Name)
            .Select(m => new CollectorItem(
                m.Agent!.Id, m.Id, m.Name, m.BranchId, m.IpAddress,
                m.Agent.LastContactAt != null && m.Agent.LastContactAt >= online, m.Agent.LastContactAt,
                db.NetworkDevices.Count(d => d.CollectorAgentId == m.Agent.Id && d.Status != NetworkDeviceStatus.Retired)
                    + db.Machines.Count(p => p.CollectorAgentId == m.Agent.Id && p.Status != MachineStatus.Retired)))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ resumen (W-42)

    public async Task<NetworkSummary> SummaryAsync(CancellationToken ct)
    {
        var devices = await queries.DevicesAsync(new DeviceFilter(), ct);
        var active = devices.Where(d => d.Status != "pasivo").ToList();
        var deviceIds = devices.Select(d => d.Id).ToList();
        var withoutCollector = await db.NetworkDevices.CountAsync(d => deviceIds.Contains(d.Id) && d.Status != NetworkDeviceStatus.Passive
            && d.Status != NetworkDeviceStatus.Retired && d.CollectorAgentId == null, ct);
        var trunks = await db.Ports.CountAsync(p => deviceIds.Contains(p.DeviceId) && p.IsTrunk, ct);
        var openAlerts = await db.Alerts.CountAsync(a => a.DeviceId != null && deviceIds.Contains(a.DeviceId.Value) && a.Status != AlertStatus.Resolved, ct);
        var collectors = await CollectorsAsync(null, ct);
        var worst = new Dictionary<string, int> { ["critico"] = 0, ["sin_respuesta"] = 1, ["advertencia"] = 2 };

        return new NetworkSummary(
            active.Count,
            active.Count(d => d.Status == "en_linea"),
            active.Count(d => d.Status == "advertencia"),
            active.Count(d => d.Status == "critico"),
            active.Count(d => d.Status == "sin_respuesta"),
            withoutCollector,
            devices.Sum(d => d.PortCount),
            devices.Sum(d => d.PortsConnected),
            devices.Sum(d => d.PortsWithProblems),
            trunks,
            (await UnknownAsync(ct)).Count(u => !u.IsRandom),
            (await DifferencesAsync(ct)).Count,
            openAlerts,
            collectors.Count(c => c.DeviceCount > 0),
            collectors.Count(c => c.DeviceCount > 0 && c.Online),
            active.Where(d => worst.ContainsKey(d.Status) || d.PortsWithProblems > 0)
                .OrderBy(d => worst.GetValueOrDefault(d.Status, 3)).ThenByDescending(d => d.PortsWithProblems).Take(10).ToList());
    }

    // ------------------------------------------------------------------ equipos desconocidos (W-53)

    public async Task<IReadOnlyList<UnknownDeviceItem>> UnknownAsync(CancellationToken ct)
    {
        var ports = await VisiblePortsAsync(ct);
        var ids = ports.Keys.ToList();
        var since = Now - RecentWindow;
        var rows = await db.MacTable.AsNoTracking()
            .Where(m => ids.Contains(m.PortId) && m.MachineId == null && m.LastSeenAt >= since)
            .ToListAsync(ct);
        var ap = await AccessPointPortsAsync(ports, ct);

        var items = new List<UnknownDeviceItem>();
        foreach (var group in rows.Where(r => !ports[r.PortId].IsTrunk).GroupBy(r => (r.PortId, Random: SnmpRules.IsRandomMac(r.MacAddress))))
        {
            var port = ports[group.Key.PortId];
            if (group.Key.Random)
            {
                // Celulares con MAC aleatoria: un solo renglón por puerto, sin alerta.
                var first = group.OrderByDescending(r => r.LastSeenAt).First();
                items.Add(new UnknownDeviceItem(first.MacAddress, null, null, first.Vlan, port.DeviceId, port.DeviceName, port.Id, port.Label,
                    true, group.Count(), group.Min(r => r.FirstSeenAt), group.Max(r => r.LastSeenAt), ap.Contains(port.Id)));
                continue;
            }
            items.AddRange(group.Select(r => new UnknownDeviceItem(r.MacAddress, r.Vendor, r.IpAddress, r.Vlan, port.DeviceId, port.DeviceName,
                port.Id, port.Label, false, 1, r.FirstSeenAt, r.LastSeenAt, ap.Contains(port.Id))));
        }
        return items.OrderBy(i => i.IsRandom).ThenByDescending(i => i.LastSeenAt).ToList();
    }

    // ------------------------------------------------------------------ buscar MAC o IP (W-52)

    public async Task<IReadOnlyList<NetworkSearchResult>> SearchAsync(string query, CancellationToken ct)
    {
        var q = query.Trim();
        if (q.Length < 2)
            return [];
        var mac = q.ToUpperInvariant().Replace('-', ':');
        var ports = await VisiblePortsAsync(ct);
        var ids = ports.Keys.ToList();

        var seen = await db.MacTable.AsNoTracking()
            .Where(m => ids.Contains(m.PortId) && (m.MacAddress.Contains(mac) || (m.IpAddress != null && m.IpAddress.Contains(q))
                || (m.MachineId != null && db.Machines.Any(x => x.Id == m.MachineId && x.Name.Contains(q)))))
            .OrderByDescending(m => m.LastSeenAt).Take(100)
            .Select(m => new { Row = m, Machine = db.Machines.Where(x => x.Id == m.MachineId).Select(x => x.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var ap = await AccessPointPortsAsync(ports, ct);
        var results = seen.Select(s =>
        {
            var port = ports[s.Row.PortId];
            return new NetworkSearchResult(s.Row.MacAddress, s.Row.IpAddress, s.Row.Vendor, s.Row.MachineId, s.Machine, port.DeviceId, port.DeviceName,
                port.Id, port.Label, s.Row.Vlan, s.Row.LastSeenAt, ap.Contains(port.Id));
        }).ToList();

        // Tarjetas de equipos con agente que ningún switch ha visto todavía: se dicen igual (sin ubicación).
        var allowed = await queries.AllowedBranchesAsync(ct);
        var cards = await db.NetworkInterfaces.AsNoTracking()
            .Where(i => (i.MacAddress.Contains(mac) || (i.IpAddress != null && i.IpAddress.Contains(q)))
                && db.Machines.Any(m => m.Id == i.MachineId && (allowed == null || allowed.Contains(m.BranchId))))
            .Take(50)
            .Select(i => new { i.MacAddress, i.IpAddress, i.MachineId, Name = db.Machines.Where(m => m.Id == i.MachineId).Select(m => m.Name).First() })
            .ToListAsync(ct);
        results.AddRange(cards.Where(c => results.All(r => r.Mac != c.MacAddress))
            .Select(c => new NetworkSearchResult(c.MacAddress, c.IpAddress, null, c.MachineId, c.Name, null, null, null, null, null, null, false)));
        return results;
    }

    // ------------------------------------------------------------------ conexión de red de un equipo (ficha W-06)

    public async Task<MachineNetworkInfo?> MachineLocationAsync(int machineId, CancellationToken ct)
    {
        var ports = await VisiblePortsAsync(ct);
        var ids = ports.Keys.ToList();
        var row = await db.MacTable.AsNoTracking()
            .Where(m => m.MachineId == machineId && ids.Contains(m.PortId))
            .OrderByDescending(m => db.Ports.Any(p => p.Id == m.PortId && p.IsManualAssignment)).ThenByDescending(m => m.LastSeenAt)
            .Select(m => new
            {
                m.PortId,
                m.Vlan,
                m.IpAddress,
                m.MacAddress,
                m.LastSeenAt,
                Rack = db.NetworkDevices.Where(d => db.Ports.Any(p => p.Id == m.PortId && p.DeviceId == d.Id)).Select(d => new { Rack = d.Rack!.Name, Room = d.Rack.Room!.Name }).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        var port = ports[row.PortId];
        var ap = await AccessPointPortsAsync(ports, ct);
        return new MachineNetworkInfo(port.DeviceId, port.DeviceName, port.Id, port.PortName, row.Vlan, row.Rack?.Rack, row.Rack?.Room,
            row.IpAddress, row.MacAddress, row.LastSeenAt, ap.Contains(port.Id), port.IsManual);
    }

    // ------------------------------------------------------------------ topología (W-51)

    /// <summary>Equipos activos y sus enlaces: los que ve LLDP y, si no, los del cableado documentado (atravesando patch panels).</summary>
    public async Task<Topology> TopologyAsync(CancellationToken ct)
    {
        var devices = (await queries.DevicesAsync(new DeviceFilter(), ct)).Where(d => d.Status != "pasivo").ToList();
        var ports = await VisiblePortsAsync(ct);
        var ids = ports.Keys.ToList();
        var graph = ports.Values.ToDictionary(p => p.Id, p => new PathPort(p.Id, p.PortName, p.DeviceId, p.DeviceName, Text(p.DeviceType), p.IsActive, p.IsTrunk));
        var cables = await db.Cables.AsNoTracking().Where(c => ids.Contains(c.PortAId))
            .Select(c => new PathLink(c.PortAId, c.PortBId, c.OutletId, c.Label, false)).ToListAsync(ct);
        var detected = await db.NetworkLinks.AsNoTracking().Where(l => ids.Contains(l.SourcePortId) && ids.Contains(l.TargetPortId))
            .Select(l => new { l.SourcePortId, l.TargetPortId }).ToListAsync(ct);

        var links = new Dictionary<(int, int), TopologyLink>();
        void Add(int portA, int portB, string kind)
        {
            var a = ports[portA];
            var b = ports[portB];
            if (a.DeviceId == b.DeviceId || !a.IsActive || !b.IsActive)
                return;
            var key = a.DeviceId < b.DeviceId ? (a.DeviceId, b.DeviceId) : (b.DeviceId, a.DeviceId);
            if (!links.ContainsKey(key) || (kind == "lldp" && links[key].Kind == "cable"))
                links[key] = new TopologyLink(a.DeviceId, b.DeviceId, a.PortName, b.PortName, kind);
        }
        foreach (var l in detected)
            Add(l.SourcePortId, l.TargetPortId, "lldp");
        foreach (var port in ports.Values.Where(p => p.IsActive && cables.Any(c => c.PortAId == p.Id || c.PortBId == p.Id)))
            if (CablePath.DocumentedPeer(port.Id, graph, cables) is int peer)
                Add(port.Id, peer, "cable");

        var visible = devices.Select(d => d.Id).ToHashSet();
        var (endpoints, clients) = await EndpointsAsync(ports, ct);
        var nodes = devices.Select(d => new TopologyNode(d.Id, d.Name, d.Type, d.Status, d.BranchName,
            ports.Values.Count(p => p.DeviceId == d.Id && p.IsTrunk), d.BranchId, d.ManagementIp, $"{d.Brand} {d.ModelName}".Trim(),
            clients.GetValueOrDefault(d.Id))).ToList();
        var readAt = devices.Max(d => d.LastPolledAt);
        return new Topology(nodes, links.Values.Where(l => visible.Contains(l.SourceId) && visible.Contains(l.TargetId)).ToList(), endpoints, readAt);
    }

    /// <summary>Lo máximo de equipos finales en el mapa (una red grande se recorre por sucursal y VLAN).</summary>
    private const int MaxEndpoints = 600;

    /// <summary>
    /// Equipos finales de cada puerto de acceso (decisión 74): las MAC vistas en 24 h en puertos que no son troncales ni
    /// llevan a un access point (lo de ahí es Wi-Fi: se cuenta como clientes del AP), más los puertos asignados a mano
    /// cuyo equipo no se ve. Las MAC aleatorias (celulares) no se dibujan.
    /// </summary>
    private async Task<(List<TopologyEndpoint> Endpoints, Dictionary<int, int> ApClients)> EndpointsAsync(
        Dictionary<int, PortInfo> ports, CancellationToken ct)
    {
        var since = Now - RecentWindow;
        var ids = ports.Keys.ToList();
        var ap = await AccessPointPortsAsync(ports, ct);
        var portRows = await db.Ports.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Number, p.Vlan, p.SpeedMbps }).ToDictionaryAsync(p => p.Id, ct);
        var macs = await db.MacTable.AsNoTracking()
            .Where(m => ids.Contains(m.PortId) && (m.LastSeenAt >= since || m.MachineId != null))
            .ToListAsync(ct);
        var machineIds = macs.Where(m => m.MachineId != null).Select(m => m.MachineId!.Value).Distinct().ToList();
        var machines = await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Name, m.Type, m.Status, m.IpAddress, HasAgent = m.Agent != null })
            .ToDictionaryAsync(m => m.Id, ct);

        var seenMacs = macs.Select(m => m.MacAddress).Distinct().ToList();
        var hostnames = (await db.IpAddresses.AsNoTracking()
                .Where(r => r.MacAddress != null && r.Hostname != null && seenMacs.Contains(r.MacAddress))
                .Select(r => new { r.MacAddress, r.Hostname, r.LastSeenAt })
                .ToListAsync(ct))
            .GroupBy(r => r.MacAddress!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.LastSeenAt).First().Hostname);

        // Clientes Wi-Fi: las MAC que se ven por el puerto que lleva a cada AP.
        var apDevices = ports.Values.Where(p => p.DeviceType == DeviceType.AccessPoint).Select(p => p.DeviceId).ToHashSet();
        var clients = new Dictionary<int, int>();
        foreach (var apId in apDevices)
        {
            var apLinkPorts = ap.Where(id => ports[id].DeviceId != apId).ToHashSet();
            clients[apId] = macs.Count(m => apLinkPorts.Contains(m.PortId) && m.LastSeenAt >= since);
        }

        var list = new List<TopologyEndpoint>();
        foreach (var m in macs.OrderBy(m => ports[m.PortId].DeviceName).ThenBy(m => portRows[m.PortId].Number))
        {
            var port = ports[m.PortId];
            if (port.IsTrunk || ap.Contains(port.Id) || !port.IsActive || (m.MachineId is null && SnmpRules.IsRandomMac(m.MacAddress)))
                continue;
            var seen = m.LastSeenAt >= since;
            // Una MAC conocida que ya no se ve solo cuenta si el puerto se asignó a mano (documentado).
            if (!seen && !port.IsManual)
                continue;
            var machine = m.MachineId is int id ? machines.GetValueOrDefault(id) : null;
            var (category, inferred) = EndpointRules.Category(machine is null ? null : Text(machine.Type), m.Vendor);
            var row = portRows[m.PortId];
            list.Add(new TopologyEndpoint(
                port.DeviceId, port.Id, row.Number, port.PortName, m.Vlan ?? row.Vlan, row.SpeedMbps, m.MacAddress, m.IpAddress ?? machine?.IpAddress,
                m.Vendor, machine?.Id, machine?.Name, machine is null ? null : Text(machine.Status), machine?.HasAgent ?? false,
                category, inferred, seen ? "detectado" : "documentado", m.FirstSeenAt, m.LastSeenAt, hostnames.GetValueOrDefault(m.MacAddress)));
            if (list.Count >= MaxEndpoints)
                break;
        }
        return (list, clients);
    }

    // ------------------------------------------------------------------ diferencias (W-66)

    /// <summary>
    /// Compara lo detectado con lo documentado: enlaces LLDP contra el cableado, y equipos detectados en puertos sin
    /// cable documentado o distintos del que un técnico asignó a mano.
    /// </summary>
    public async Task<IReadOnlyList<DifferenceItem>> DifferencesAsync(CancellationToken ct)
    {
        var ports = await VisiblePortsAsync(ct);
        var ids = ports.Keys.ToList();
        var graph = ports.Values.ToDictionary(p => p.Id, p => new PathPort(p.Id, p.PortName, p.DeviceId, p.DeviceName, Text(p.DeviceType), p.IsActive, p.IsTrunk));
        var cables = await db.Cables.AsNoTracking().Where(c => ids.Contains(c.PortAId))
            .Select(c => new PathLink(c.PortAId, c.PortBId, c.OutletId, c.Label, false)).ToListAsync(ct);
        var links = await db.NetworkLinks.AsNoTracking().Where(l => ids.Contains(l.SourcePortId) && ids.Contains(l.TargetPortId))
            .Select(l => new { l.SourcePortId, l.TargetPortId }).ToListAsync(ct);

        var items = new List<DifferenceItem>();
        var reported = new HashSet<(int, int)>();
        foreach (var link in links)
        {
            var pair = link.SourcePortId < link.TargetPortId ? (link.SourcePortId, link.TargetPortId) : (link.TargetPortId, link.SourcePortId);
            if (!reported.Add(pair))
                continue;
            var a = ports[link.SourcePortId];
            var b = ports[link.TargetPortId];
            var fromA = CablePath.DocumentedPeer(a.Id, graph, cables);
            var fromB = CablePath.DocumentedPeer(b.Id, graph, cables);
            if (fromA == b.Id || fromB == a.Id)
                continue;
            if (fromA is null && fromB is null)
                items.Add(new DifferenceItem("enlace_sin_cable", "Enlace sin cable documentado",
                    $"LLDP ve {a.Label} conectado a {b.Label}, pero ese cable no está documentado.", a.DeviceId, a.Id, null));
            else
            {
                var (from, to) = fromA is int x ? (a, x) : (b, fromB!.Value);
                items.Add(new DifferenceItem("cable_distinto", "El cable no llega a donde dice la documentación",
                    $"Según el cableado, {from.Label} llega a {ports.GetValueOrDefault(to)?.Label ?? "otro puerto"}; LLDP ve {a.Label} conectado a {b.Label}.",
                    from.DeviceId, from.Id, null));
            }
        }

        var since = Now - RecentWindow;
        var cabled = cables.SelectMany(c => c.PortBId is int other ? new[] { c.PortAId, other } : [c.PortAId]).ToHashSet();
        var ap = await AccessPointPortsAsync(ports, ct);
        var detected = await db.MacTable.AsNoTracking()
            .Where(m => ids.Contains(m.PortId) && m.MachineId != null && m.LastSeenAt >= since)
            .Select(m => new { m.PortId, m.MachineId, m.MacAddress, Name = db.Machines.Where(x => x.Id == m.MachineId).Select(x => x.Name).First() })
            .ToListAsync(ct);
        foreach (var group in detected.GroupBy(d => d.PortId))
        {
            var port = ports[group.Key];
            if (port.IsTrunk || ap.Contains(port.Id) || !port.IsActive)
                continue;
            if (!cabled.Contains(port.Id))
            {
                var names = string.Join(", ", group.Select(g => g.Name).Distinct());
                items.Add(new DifferenceItem("equipo_sin_cable", "Equipo en un puerto sin cable documentado",
                    $"{names} está conectado a {port.Label}, pero ese puerto no tiene cable documentado.", port.DeviceId, port.Id, group.First().MachineId));
            }
        }
        return items;
    }
}

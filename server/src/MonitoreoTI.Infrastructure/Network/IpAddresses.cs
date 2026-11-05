using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Direcciones IP del panel (decisión 72): subredes con el estado de cada dirección, reservas y "Escanear ahora".
/// Ver: red.ver; cambiar y escanear: red.editar. Cada usuario ve las subredes de sus sucursales.
/// </summary>
public sealed class IpAddresses(
    MonitoreoDbContext db, NetworkQueries queries, IAuditTrail audit, IIpSweepDispatcher dispatcher, TimeProvider clock)
{
    /// <summary>Un recolector que habló en este tiempo está en línea (como en la lista de recolectores).</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<IQueryable<Subnet>> VisibleAsync(CancellationToken ct)
    {
        var allowed = await queries.AllowedBranchesAsync(ct);
        return allowed is null ? db.Subnets : db.Subnets.Where(s => allowed.Contains(s.BranchId));
    }

    // ------------------------------------------------------------------ consultas

    public async Task<IReadOnlyList<SubnetItem>> SubnetsAsync(int? branchId, CancellationToken ct)
    {
        var subnets = await (await VisibleAsync(ct)).AsNoTracking()
            .Where(s => branchId == null || s.BranchId == branchId)
            .OrderBy(s => s.Name).ToListAsync(ct);
        var items = new List<SubnetItem>();
        foreach (var subnet in subnets)
            items.Add((await BuildAsync(subnet, ct)).Subnet);
        return items;
    }

    public async Task<SubnetDetail?> SubnetAsync(int id, CancellationToken ct)
    {
        var subnet = await (await VisibleAsync(ct)).AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return subnet is null ? null : await BuildAsync(subnet, ct);
    }

    /// <summary>
    /// El estado de cada dirección de la subred: lo que se vio por ping o ARP, lo que reportan los agentes de los equipos
    /// (su IP, aunque el ping no pase por su firewall) y las reservas.
    /// </summary>
    private async Task<SubnetDetail> BuildAsync(Subnet subnet, CancellationToken ct)
    {
        var now = Now;
        IpRules.TryParse(subnet.Network, out var network);
        var prefix = (int)subnet.Prefix;
        uint? gateway = IpRules.TryParse(subnet.Gateway, out var g) ? g : null;

        var rows = await db.IpAddresses.AsNoTracking().Where(r => r.SubnetId == subnet.Id).ToListAsync(ct);
        var byIp = rows.Where(r => IpRules.TryParse(r.Ip, out _)).ToDictionary(r => Parse(r.Ip));

        var reservations = await db.IpReservations.AsNoTracking().Where(r => r.SubnetId == subnet.Id).OrderBy(r => r.Id).ToListAsync(ct);
        var ranges = reservations
            .Select(r => (Item: r, From: Parse(r.From), To: Parse(r.To)))
            .ToList();

        // IP que reportan los equipos: tarjetas de los agentes y la IP de la ficha (impresoras sin agente). Se filtra por
        // los dos primeros octetos en la base y por la subred aquí.
        var head = string.Join('.', IpRules.ToText(network).Split('.').Take(2)) + ".";
        var cards = await db.NetworkInterfaces.AsNoTracking()
            .Where(i => i.IpAddress != null && i.IpAddress.StartsWith(head))
            .Join(db.Machines.Where(m => m.Status != MachineStatus.Retired), i => i.MachineId, m => m.Id,
                (i, m) => new { Ip = i.IpAddress!, MachineId = m.Id, m.Name, m.LastReportAt })
            .ToListAsync(ct);
        var fiches = await db.Machines.AsNoTracking()
            .Where(m => m.Status != MachineStatus.Retired && m.IpAddress != null && m.IpAddress.StartsWith(head))
            .Select(m => new { Ip = m.IpAddress!, MachineId = m.Id, m.Name, m.LastReportAt })
            .ToListAsync(ct);
        var reported = cards.Concat(fiches)
            .Where(c => IpRules.TryParse(c.Ip, out var ip) && IpRules.Contains(network, prefix, ip))
            .GroupBy(c => Parse(c.Ip))
            .ToDictionary(grp => grp.Key, grp => grp.OrderByDescending(c => c.LastReportAt).First());

        var machineIds = rows.Where(r => r.MachineId != null).Select(r => r.MachineId!.Value).Distinct().ToList();
        var machineNames = await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        // Dónde se ve cada MAC en los switches (el puerto más reciente).
        var macs = rows.Where(r => r.MacAddress != null).Select(r => r.MacAddress!).Distinct().ToList();
        var ports = (await db.MacTable.AsNoTracking().Where(m => macs.Contains(m.MacAddress))
                .Select(m => new
                {
                    m.MacAddress,
                    m.LastSeenAt,
                    Label = db.Ports.Where(p => p.Id == m.PortId).Select(p => p.Device!.Name + " " + (p.Name ?? p.Number.ToString())).First(),
                })
                .ToListAsync(ct))
            .GroupBy(m => m.MacAddress)
            .ToDictionary(grp => grp.Key, grp => grp.OrderByDescending(m => m.LastSeenAt).First().Label);

        var items = new List<IpItem>();
        var states = new List<(uint Ip, IpState State)>();
        foreach (var ip in IpRules.Hosts(network, prefix))
        {
            var row = byIp.GetValueOrDefault(ip);
            var agent = reported.GetValueOrDefault(ip);
            var reservation = ranges.FirstOrDefault(r => ip >= r.From && ip <= r.To).Item;
            var isGateway = gateway == ip;

            DateTime? lastSeen = Max(row?.LastSeenAt, agent?.LastReportAt);
            int? machineId = row?.MachineId ?? agent?.MachineId;
            var machineName = row?.MachineId is int id ? machineNames.GetValueOrDefault(id) : agent?.Name;
            var source = row?.MachineId is not null ? "mac" : agent is not null ? "agente" : null;

            var state = IpRules.Classify(lastSeen, reservation is not null || isGateway, machineId is not null, subnet.FreeAfterDays, now);
            states.Add((ip, state));
            items.Add(new IpItem(
                IpRules.ToText(ip), IpRules.Text(state), row?.MacAddress, row?.Vendor, machineId, machineName, source,
                row?.MacAddress is string mac ? ports.GetValueOrDefault(mac) : null,
                row?.FirstSeenAt, lastSeen, row?.LastPingAt,
                isGateway ? "Puerta de enlace" : reservation?.Reason,
                isGateway,
                row?.MacChangedAt is DateTime changed && changed >= now - IpRules.MacChangeWindow,
                row?.MacChangedAt is DateTime c2 && c2 >= now - IpRules.MacChangeWindow ? row.PreviousMacAddress : null,
                row?.Hostname, row?.HostnameSource, row?.Interface,
                row?.OperatingSystem,
                row?.Dhcp switch
                {
                    true when row.DhcpExpiresAt is null || row.DhcpExpiresAt > now => "dhcp",
                    false => "fija",
                    _ => null,
                },
                row?.Dhcp == true ? row.DhcpExpiresAt : null));
        }

        var collector = subnet.CollectorAgentId is int agentId
            ? await db.Agents.AsNoTracking().Where(a => a.Id == agentId)
                .Select(a => new { Name = db.Machines.Where(m => m.Id == a.MachineId).Select(m => m.Name).FirstOrDefault(), a.LastContactAt, a.Version })
                .FirstOrDefaultAsync(ct)
            : null;
        var arpDevice = subnet.ArpDeviceId is int deviceId
            ? await db.NetworkDevices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        var branch = await db.Branches.AsNoTracking().Where(b => b.Id == subnet.BranchId).Select(b => b.Name).FirstOrDefaultAsync(ct);

        int Count(IpState s) => states.Count(x => x.State == s);
        var canSweep = IpRules.SupportsSweep(collector?.Version);
        var item = new SubnetItem(
            subnet.Id, subnet.Name, subnet.BranchId, branch ?? "", $"{subnet.Network}/{subnet.Prefix}", subnet.Vlan, subnet.Gateway,
            subnet.CollectorAgentId, collector?.Name, collector?.LastContactAt is DateTime contact && contact >= now - OnlineWindow, canSweep,
            subnet.ArpDeviceId, arpDevice, subnet.SweepHours, subnet.FreeAfterDays, subnet.LastSweepAt,
            canSweep && subnet.SweepRequestedAt is DateTime asked && asked > now - IpSweeps.RequestTimeout ? asked : null, subnet.LastError,
            states.Count, Count(IpState.InUse) + Count(IpState.Unidentified), Count(IpState.Unidentified), Count(IpState.Recent),
            Count(IpState.Reserved), Count(IpState.Free));

        var reservationItems = ranges.Select(r => new IpReservationItem(r.Item.Id, r.Item.From, r.Item.To, r.Item.Reason, (int)(r.To - r.From + 1), r.Item.Origin)).ToList();
        var suggested = IpRules.SuggestFree(states) is uint free ? IpRules.ToText(free) : null;
        return new SubnetDetail(item, items, reservationItems, suggested);
    }

    /// <summary>
    /// Quién tuvo una dirección en los últimos días, de lo más reciente a lo más viejo. El período vigente de un equipo
    /// con agente llega hasta su último reporte si aún tiene esa IP (el agente reporta sus tarjetas cada pocas horas).
    /// </summary>
    public async Task<IReadOnlyList<IpHistoryItem>?> HistoryAsync(int subnetId, string ip, int days, CancellationToken ct)
    {
        if (!await (await VisibleAsync(ct)).AnyAsync(s => s.Id == subnetId, ct) || !IpRules.TryParse(ip, out var value))
            return null;
        var text = IpRules.ToText(value);
        var now = Now;
        var since = now.AddDays(-Math.Clamp(days, 1, 90));
        var rows = await db.IpHistory.AsNoTracking()
            .Where(h => h.SubnetId == subnetId && h.Ip == text && h.To >= since)
            .OrderByDescending(h => h.To).Take(200).ToListAsync(ct);

        var machineIds = rows.Where(r => r.MachineId != null).Select(r => r.MachineId!.Value).Distinct().ToList();
        var stillThere = await db.NetworkInterfaces.AsNoTracking()
            .Where(i => machineIds.Contains(i.MachineId) && i.IpAddress == text)
            .Join(db.Machines, i => i.MachineId, m => m.Id, (i, m) => new { m.Id, m.Name, m.LastReportAt })
            .ToListAsync(ct);

        return rows.Select((r, index) =>
        {
            var to = r.To;
            var name = r.MachineName;
            if (r.MachineId is int id && stillThere.FirstOrDefault(m => m.Id == id) is { } m)
            {
                name = m.Name;
                if (index == 0 && m.LastReportAt > to)
                    to = m.LastReportAt.Value;
            }
            return new IpHistoryItem(r.MacAddress, r.Vendor, r.MachineId, name, SourceText(r.Source), r.From, to, to >= now - IpRules.InUseWindow);
        }).ToList();
    }

    private static string SourceText(IpSource source) => source switch
    {
        IpSource.Ping => "ping",
        IpSource.Agent => "agente",
        _ => "arp",
    };

    // ------------------------------------------------------------------ cambios

    public async Task<CommandResult> SaveSubnetAsync(int? id, SubnetInput input, CancellationToken ct)
    {
        var allowed = await queries.AllowedBranchesAsync(ct);
        if ((allowed is not null && !allowed.Contains(input.SucursalId)) || !await db.Branches.AnyAsync(b => b.Id == input.SucursalId, ct))
            return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        Subnet? subnet = null;
        if (id is int subnetId && (subnet = await (await VisibleAsync(ct)).FirstOrDefaultAsync(s => s.Id == subnetId, ct)) is null)
            return CommandResult.NotFound();
        if (!IpRules.TryParseCidr(input.Red, out var network, out var prefix))
            return CommandResult.Invalid("La red no es válida.");

        // Dos subredes que se pisan harían que una dirección cuente en las dos.
        var others = await db.Subnets.AsNoTracking().Where(s => s.Id != (id ?? 0)).Select(s => new { s.Name, s.Network, s.Prefix }).ToListAsync(ct);
        foreach (var other in others)
        {
            if (!IpRules.TryParse(other.Network, out var otherNetwork))
                continue;
            var shorter = Math.Min(prefix, other.Prefix);
            if ((network & IpRules.Mask(shorter)) == (otherNetwork & IpRules.Mask(shorter)))
                return CommandResult.Conflict($"La red se superpone con la subred {other.Name} ({other.Network}/{other.Prefix}).");
        }

        string? gateway = null;
        if (!string.IsNullOrWhiteSpace(input.PuertaEnlace))
        {
            if (!IpRules.TryParse(input.PuertaEnlace, out var g) || !IpRules.IsValidRange(network, prefix, g, g))
                return CommandResult.Invalid("La puerta de enlace debe ser una dirección de la subred.");
            gateway = IpRules.ToText(g);
        }
        if (input.RecolectorId is int agentId && !await db.Agents.AnyAsync(a => a.Id == agentId && a.IsApproved && !a.IsRevoked, ct))
            return CommandResult.Invalid("El recolector no existe o no está aprobado.");
        if (input.DispositivoArpId is int deviceId
            && !await (await queries.VisibleDevicesAsync(ct)).AnyAsync(d => d.Id == deviceId && d.ManagementIp != null, ct))
            return CommandResult.Invalid("El equipo para leer la ARP no existe o no tiene IP de gestión.");

        var before = subnet is null ? null : Snapshot(subnet);
        subnet ??= db.Subnets.Add(new Subnet()).Entity;
        subnet.Name = input.Nombre.Trim();
        subnet.BranchId = input.SucursalId;
        var changedRange = subnet.Network != IpRules.ToText(network) || subnet.Prefix != prefix;
        subnet.Network = IpRules.ToText(network);
        subnet.Prefix = (byte)prefix;
        subnet.Vlan = input.Vlan;
        subnet.Gateway = gateway;
        subnet.CollectorAgentId = input.RecolectorId;
        subnet.ArpDeviceId = input.DispositivoArpId;
        subnet.SweepHours = (short)input.BarridoHoras;
        subnet.FreeAfterDays = (short)input.DiasLibre;
        if (changedRange && id is not null)
        {
            // Otra red: lo visto y lo reservado ya no corresponde.
            db.IpAddresses.RemoveRange(db.IpAddresses.Where(r => r.SubnetId == subnet.Id));
            db.IpReservations.RemoveRange(db.IpReservations.Where(r => r.SubnetId == subnet.Id));
            subnet.LastSweepAt = null;
        }
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(before is null ? "subred.crear" : "subred.editar", "subredes", subnet.Id.ToString(), before, Snapshot(subnet), ct);
        return CommandResult.Ok(subnet.Id);
    }

    public async Task<CommandResult> DeleteSubnetAsync(int id, CancellationToken ct)
    {
        var subnet = await (await VisibleAsync(ct)).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (subnet is null)
            return CommandResult.NotFound();
        db.Subnets.Remove(subnet);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("subred.eliminar", "subredes", id.ToString(), Snapshot(subnet), null, ct);
        return CommandResult.Ok(id);
    }

    public async Task<CommandResult> AddReservationAsync(int subnetId, IpReservationInput input, CancellationToken ct)
    {
        var subnet = await (await VisibleAsync(ct)).AsNoTracking().FirstOrDefaultAsync(s => s.Id == subnetId, ct);
        if (subnet is null)
            return CommandResult.NotFound();
        IpRules.TryParse(subnet.Network, out var network);
        if (!IpRules.TryParse(input.Desde, out var from) || !IpRules.TryParse(input.Hasta, out var to)
            || !IpRules.IsValidRange(network, subnet.Prefix, from, to))
            return CommandResult.Invalid($"El rango debe estar dentro de {subnet.Network}/{subnet.Prefix} y la IP inicial no puede ser mayor que la final.");
        var reservation = db.IpReservations.Add(new IpReservation
        {
            SubnetId = subnetId,
            From = IpRules.ToText(from),
            To = IpRules.ToText(to),
            Reason = input.Motivo.Trim(),
        }).Entity;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("subred.reservar", "subredes", subnetId.ToString(), null,
            new { desde = reservation.From, hasta = reservation.To, motivo = reservation.Reason }, ct);
        return CommandResult.Ok(reservation.Id);
    }

    public async Task<CommandResult> DeleteReservationAsync(int subnetId, int id, CancellationToken ct)
    {
        if (!await (await VisibleAsync(ct)).AnyAsync(s => s.Id == subnetId, ct))
            return CommandResult.NotFound();
        var reservation = await db.IpReservations.FirstOrDefaultAsync(r => r.Id == id && r.SubnetId == subnetId, ct);
        if (reservation is null)
            return CommandResult.NotFound();
        if (reservation.Origin == IpReservationOrigins.FortiGate)
            return CommandResult.Invalid("Esta reserva viene del DHCP del FortiGate: se cambia allá y aquí se actualiza sola.");
        db.IpReservations.Remove(reservation);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("subred.liberar_reserva", "subredes", subnetId.ToString(),
            new { desde = reservation.From, hasta = reservation.To, motivo = reservation.Reason }, null, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>"Escanear ahora": se pide al recolector si está conectado; el resultado llega en segundos o minutos.</summary>
    public async Task<CommandResult> RequestSweepAsync(int id, CancellationToken ct)
    {
        var subnet = await (await VisibleAsync(ct)).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (subnet is null)
            return CommandResult.NotFound();
        if (subnet.CollectorAgentId is not int agentId || await IpInventory.RequestAsync(db, subnet, ct) is not { } request)
            return CommandResult.Invalid("Asigne un recolector a la subred para poder escanearla.");
        var agent = await db.Agents.AsNoTracking().Where(a => a.Id == agentId)
            .Select(a => new { a.Version, Name = db.Machines.Where(m => m.Id == a.MachineId).Select(m => m.Name).FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (!IpRules.SupportsSweep(agent?.Version))
            return CommandResult.Invalid($"El agente de {agent?.Name ?? "el recolector"} es la versión {agent?.Version ?? "?"}: actualícelo a la 0.3.4 o posterior para barrer con ping. Mientras tanto, la subred se llena con la ARP del firewall.");
        if (!await dispatcher.SendAsync(subnet.CompanyId, agentId, request, ct))
            return CommandResult.Conflict("El recolector no está conectado: inténtelo cuando vuelva a estar en línea.");
        subnet.SweepRequestedAt = Now;
        await db.SaveChangesAsync(ct);
        return CommandResult.Ok(id);
    }

    private static object Snapshot(Subnet s) => new
    {
        nombre = s.Name,
        red = $"{s.Network}/{s.Prefix}",
        sucursalId = s.BranchId,
        vlan = s.Vlan,
        puertaEnlace = s.Gateway,
        recolectorId = s.CollectorAgentId,
        dispositivoArpId = s.ArpDeviceId,
        barridoHoras = s.SweepHours,
        diasLibre = s.FreeAfterDays,
    };

    private static uint Parse(string text) => IpRules.TryParse(text, out var ip) ? ip : 0;

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}

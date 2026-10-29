using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Lecturas del data center (W-45 a W-65). Todo cuelga de una sucursal: la sala, y por ella el gabinete; el
/// dispositivo y la roseta tienen la suya. Quien tiene sucursales asignadas solo ve las suyas.
/// </summary>
public sealed class NetworkQueries(MonitoreoDbContext db, ICurrentUser user)
{
    public async Task<IReadOnlyCollection<int>?> AllowedBranchesAsync(CancellationToken ct) => await user.AllowedBranchIdsAsync(ct);

    public async Task<IQueryable<Room>> VisibleRoomsAsync(CancellationToken ct)
    {
        var allowed = await AllowedBranchesAsync(ct);
        return allowed is null ? db.Rooms : db.Rooms.Where(r => allowed.Contains(r.BranchId));
    }

    public async Task<IQueryable<Rack>> VisibleRacksAsync(CancellationToken ct)
    {
        var rooms = (await VisibleRoomsAsync(ct)).Select(r => r.Id);
        return db.Racks.Where(r => rooms.Contains(r.RoomId));
    }

    /// <summary>Dispositivos visibles; los dados de baja solo si se piden.</summary>
    public async Task<IQueryable<NetworkDevice>> VisibleDevicesAsync(CancellationToken ct, bool includeRetired = false)
    {
        var allowed = await AllowedBranchesAsync(ct);
        var devices = allowed is null ? db.NetworkDevices : db.NetworkDevices.Where(d => allowed.Contains(d.BranchId));
        return includeRetired ? devices : devices.Where(d => d.Status != NetworkDeviceStatus.Retired);
    }

    public async Task<IQueryable<NetworkOutlet>> VisibleOutletsAsync(CancellationToken ct)
    {
        var allowed = await AllowedBranchesAsync(ct);
        return allowed is null ? db.NetworkOutlets : db.NetworkOutlets.Where(o => allowed.Contains(o.BranchId));
    }

    private static bool IsProblem(PortStatus status) => status is PortStatus.Errors or PortStatus.Down;

    // ------------------------------------------------------------------ salas y gabinetes

    public async Task<IReadOnlyList<RoomItem>> RoomsAsync(CancellationToken ct)
    {
        var devices = await VisibleDevicesAsync(ct);
        return await (await VisibleRoomsAsync(ct)).AsNoTracking()
            .OrderBy(r => r.Name)
            .Select(r => new RoomItem(
                r.Id, r.Name, r.Location, r.BranchId,
                db.Branches.Where(b => b.Id == r.BranchId).Select(b => b.Name).First(),
                db.Racks.Count(k => k.RoomId == r.Id),
                devices.Count(d => d.Rack != null && d.Rack.RoomId == r.Id)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<RackItem>> RacksAsync(int? roomId, CancellationToken ct)
    {
        var racks = (await VisibleRacksAsync(ct)).AsNoTracking();
        if (roomId is int room)
            racks = racks.Where(r => r.RoomId == room);
        var devices = await VisibleDevicesAsync(ct);
        return await racks
            .OrderBy(r => r.Room!.Name).ThenBy(r => r.Row).ThenBy(r => r.Position).ThenBy(r => r.Name)
            .Select(r => new RackItem(
                r.Id, r.Name, r.RoomId, r.Room!.Name, r.Room.BranchId,
                db.Branches.Where(b => b.Id == r.Room.BranchId).Select(b => b.Name).First(),
                r.HeightUnits,
                devices.Where(d => d.RackId == r.Id).Sum(d => (int?)d.HeightUnits) ?? 0,
                r.Row, r.Position, r.IsActive,
                devices.Count(d => d.RackId == r.Id),
                r.QrCode))
            .ToListAsync(ct);
    }

    public async Task<RackDetail?> RackAsync(int id, CancellationToken ct)
    {
        var rack = (await RacksAsync(null, ct)).FirstOrDefault(r => r.Id == id);
        if (rack is null)
            return null;
        var devices = await (await VisibleDevicesAsync(ct)).AsNoTracking()
            .Where(d => d.RackId == id && d.RackPositionU != null)
            .OrderByDescending(d => d.RackPositionU)
            .Select(d => new
            {
                d.Id, d.Name, d.Type, Model = d.Model!.ModelName, d.RackPositionU, d.HeightUnits, d.Status,
                Ports = d.Ports.Count,
                Connected = d.Ports.Count(p => p.Status == PortStatus.Connected),
                Problems = d.Ports.Count(p => p.Status == PortStatus.Errors || p.Status == PortStatus.Down),
            })
            .ToListAsync(ct);
        return new RackDetail(rack, devices.Select(d => new RackDevice(
            d.Id, d.Name, Text(d.Type), d.Model, d.RackPositionU!.Value, d.HeightUnits, Text(d.Status), d.Ports, d.Connected, d.Problems)).ToList());
    }

    // ------------------------------------------------------------------ catálogo

    public async Task<IReadOnlyList<DeviceModelItem>> ModelsAsync(CancellationToken ct)
    {
        var rows = await db.DeviceModels.AsNoTracking().OrderBy(m => m.Brand).ThenBy(m => m.ModelName).ToListAsync(ct);
        return rows.Select(m => new DeviceModelItem(
            m.Id, m.Brand, m.ModelName, Text(m.Type), m.HeightUnits, m.PortCount, m.PortDescription, m.IsActiveDevice, m.CompanyId is null)).ToList();
    }

    // ------------------------------------------------------------------ dispositivos

    public async Task<IReadOnlyList<DeviceListItem>> DevicesAsync(DeviceFilter filter, CancellationToken ct)
    {
        var devices = (await VisibleDevicesAsync(ct)).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            devices = devices.Where(d => d.Name.Contains(q) || (d.ManagementIp != null && d.ManagementIp.Contains(q)) || d.Model!.ModelName.Contains(q));
        }
        if (filter.Type is { } typeText && NetworkValues.DeviceTypes.Contains(typeText))
        {
            var type = Persistence.Conversions.EnumMemberConverter<DeviceType>.FromProvider(typeText);
            devices = devices.Where(d => d.Type == type);
        }
        if (filter.BranchId is int branch)
            devices = devices.Where(d => d.BranchId == branch);
        if (filter.RoomId is int room)
            devices = devices.Where(d => d.Rack != null && d.Rack.RoomId == room);
        if (filter.RackId is int rackId)
            devices = devices.Where(d => d.RackId == rackId);

        var rows = await devices
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                Device = d,
                d.Model!.Brand,
                Model = d.Model.ModelName,
                Branch = db.Branches.Where(b => b.Id == d.BranchId).Select(b => b.Name).First(),
                Rack = d.Rack!.Name,
                Room = d.Rack.Room!.Name,
                Ports = d.Ports.Count,
                Connected = d.Ports.Count(p => p.Status == PortStatus.Connected),
                Problems = d.Ports.Count(p => p.Status == PortStatus.Errors || p.Status == PortStatus.Down),
            })
            .ToListAsync(ct);

        return rows.Select(r => new DeviceListItem(
            r.Device.Id, r.Device.Name, Text(r.Device.Type), r.Brand, r.Model, r.Device.BranchId, r.Branch,
            r.Device.RackId, r.Device.RackId is null ? null : r.Rack, r.Device.RackId is null ? null : r.Room,
            r.Device.RackPositionU, r.Device.HeightUnits, r.Device.ManagementIp, Text(r.Device.Status), r.Device.LastPolledAt,
            r.Ports, r.Connected, r.Problems)).ToList();
    }

    public async Task<DeviceDetail?> DeviceAsync(int id, CancellationToken ct)
    {
        var row = await (await VisibleDevicesAsync(ct, includeRetired: true)).AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new
            {
                Device = d,
                d.Model!.Brand,
                Model = d.Model.ModelName,
                d.Model.IsActiveDevice,
                Branch = db.Branches.Where(b => b.Id == d.BranchId).Select(b => b.Name).First(),
                Rack = d.Rack!.Name,
                RoomId = (int?)d.Rack.RoomId,
                Room = d.Rack.Room!.Name,
            })
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;

        var d = row.Device;
        var labels = await PortLabelsAsync(ct);
        var cables = await db.Cables.AsNoTracking()
            .Where(c => db.Ports.Any(p => p.DeviceId == id && (p.Id == c.PortAId || p.Id == c.PortBId)))
            .ToListAsync(ct);
        var outlets = await db.NetworkOutlets.AsNoTracking()
            .Where(o => cables.Select(c => c.OutletId).Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Code, ct);
        var machines = await db.MacTable.AsNoTracking()
            .Where(m => m.MachineId != null && db.Ports.Any(p => p.Id == m.PortId && p.DeviceId == id))
            .GroupBy(m => m.PortId)
            .Select(g => g.OrderByDescending(m => m.LastSeenAt).Select(m => new { m.PortId, m.MachineId, Name = db.Machines.Where(x => x.Id == m.MachineId).Select(x => x.Name).FirstOrDefault() }).First())
            .ToListAsync(ct);

        var ports = await db.Ports.AsNoTracking().Where(p => p.DeviceId == id).OrderBy(p => p.Number).ToListAsync(ct);
        var items = ports.Select(p =>
        {
            var cable = cables.FirstOrDefault(c => c.PortAId == p.Id && c.OutletId is null) ?? cables.FirstOrDefault(c => c.PortAId == p.Id || c.PortBId == p.Id);
            // En un patch panel el cable al switch dice más que el de la roseta.
            string? to = cable is null ? null
                : cable.OutletId is int outlet ? $"Roseta {outlets.GetValueOrDefault(outlet, "?")}"
                : labels.GetValueOrDefault(cable.PortAId == p.Id ? cable.PortBId!.Value : cable.PortAId);
            var outletCable = cables.FirstOrDefault(c => c.PortAId == p.Id && c.OutletId is not null);
            if (outletCable is not null && cable != outletCable)
                to = $"{to} · Roseta {outlets.GetValueOrDefault(outletCable.OutletId!.Value, "?")}";
            var machine = machines.FirstOrDefault(m => m.PortId == p.Id);
            return new PortItem(
                p.Id, p.Number, p.Name, Text(p.Type), p.Vlan, p.SpeedMbps, Text(p.Status), p.IsTrunk, p.IsVmHost, p.IsManualAssignment,
                cable?.Id, to, machine?.MachineId, machine?.Name);
        }).ToList();

        return new DeviceDetail(
            d.Id, d.Name, Text(d.Type), d.ModelId, row.Brand, row.Model, row.IsActiveDevice, d.BranchId, row.Branch,
            d.RackId, d.RackId is null ? null : row.Rack, d.RackId is null ? null : row.RoomId, d.RackId is null ? null : row.Room,
            d.RackPositionU, d.HeightUnits, d.ManagementIp, d.Firmware, Text(d.Status), d.LastPolledAt,
            d.SnmpUser, d.SnmpEncryptedCredential is not null, d.QrCode, d.CreatedAt, items);
    }

    /// <summary>"SW-ACC-02 Gi1/0/12" para cada puerto visible.</summary>
    private async Task<Dictionary<int, string>> PortLabelsAsync(CancellationToken ct)
    {
        var devices = (await VisibleDevicesAsync(ct, includeRetired: true)).Select(d => d.Id);
        return await db.Ports.AsNoTracking().Where(p => devices.Contains(p.DeviceId))
            .Select(p => new { p.Id, Label = p.Device!.Name + " " + (p.Name ?? p.Number.ToString()) })
            .ToDictionaryAsync(p => p.Id, p => p.Label, ct);
    }

    // ------------------------------------------------------------------ rosetas, cables y ruta

    public async Task<IReadOnlyList<OutletItem>> OutletsAsync(int? branchId, string? query, CancellationToken ct)
    {
        var outlets = (await VisibleOutletsAsync(ct)).AsNoTracking();
        if (branchId is int branch)
            outlets = outlets.Where(o => o.BranchId == branch);
        if (!string.IsNullOrWhiteSpace(query))
            outlets = outlets.Where(o => o.Code.Contains(query.Trim()) || (o.Location != null && o.Location.Contains(query.Trim())));

        var rows = await outlets.OrderBy(o => o.Code)
            .Select(o => new
            {
                Outlet = o,
                Branch = db.Branches.Where(b => b.Id == o.BranchId).Select(b => b.Name).First(),
                Cable = db.Cables.Where(c => c.OutletId == o.Id).Select(c => new { c.Id, c.PortAId }).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var labels = await PortLabelsAsync(ct);
        return rows.Select(r => new OutletItem(
            r.Outlet.Id, r.Outlet.Code, r.Outlet.Location, r.Outlet.BranchId, r.Branch, r.Cable?.Id, r.Cable?.PortAId,
            r.Cable is null ? null : labels.GetValueOrDefault(r.Cable.PortAId), r.Outlet.QrCode)).ToList();
    }

    public async Task<IReadOnlyList<CableItem>> CablesAsync(int? deviceId, CancellationToken ct)
    {
        var labels = await PortLabelsAsync(ct);
        var cables = db.Cables.AsNoTracking().Where(c => labels.Keys.Contains(c.PortAId));
        if (deviceId is int device)
            cables = cables.Where(c => db.Ports.Any(p => p.DeviceId == device && (p.Id == c.PortAId || p.Id == c.PortBId)));
        var rows = await cables.OrderBy(c => c.Id)
            .Select(c => new { Cable = c, Outlet = db.NetworkOutlets.Where(o => o.Id == c.OutletId).Select(o => o.Code).FirstOrDefault() })
            .ToListAsync(ct);
        return rows.Select(r => new CableItem(
            r.Cable.Id, r.Cable.PortAId, labels.GetValueOrDefault(r.Cable.PortAId, "?"), r.Cable.PortBId,
            r.Cable.PortBId is int b ? labels.GetValueOrDefault(b, "?") : null, r.Cable.OutletId, r.Outlet,
            r.Cable.CableType is { } type ? Text(type) : null, r.Cable.Color, r.Cable.Label, r.Cable.Notes, r.Cable.CreatedAt)).ToList();
    }

    /// <summary>Ruta completa desde una roseta o un puerto (W-65). null si el origen no existe o no es visible.</summary>
    public async Task<IReadOnlyList<PathHop>?> PathAsync(int? outletId, int? portId, CancellationToken ct)
    {
        var devices = await (await VisibleDevicesAsync(ct, includeRetired: true)).AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.Type, d.Model!.IsActiveDevice })
            .ToDictionaryAsync(d => d.Id, ct);
        var ports = await db.Ports.AsNoTracking().Where(p => devices.Keys.Contains(p.DeviceId))
            .Select(p => new { p.Id, p.Name, p.Number, p.DeviceId, p.IsTrunk })
            .ToListAsync(ct);
        var graph = ports.ToDictionary(p => p.Id, p =>
        {
            var device = devices[p.DeviceId];
            return new PathPort(p.Id, p.Name ?? p.Number.ToString(), p.DeviceId, device.Name, Text(device.Type), device.IsActiveDevice, p.IsTrunk);
        });

        var cables = await db.Cables.AsNoTracking().Select(c => new PathLink(c.PortAId, c.PortBId, c.OutletId, c.Label, false)).ToListAsync(ct);
        var detected = await db.NetworkLinks.AsNoTracking().Select(l => new PathLink(l.SourcePortId, l.TargetPortId, null, null, true)).ToListAsync(ct);
        var links = cables.Concat(detected).Where(l => graph.ContainsKey(l.PortAId)).ToList();

        if (outletId is int outlet)
        {
            var code = await (await VisibleOutletsAsync(ct)).Where(o => o.Id == outlet).Select(o => o.Code).FirstOrDefaultAsync(ct);
            return code is null ? null : CablePath.FromOutlet(outlet, code, graph, links);
        }
        if (portId is int port && graph.ContainsKey(port))
        {
            var outletCodes = await (await VisibleOutletsAsync(ct)).ToDictionaryAsync(o => o.Id, o => o.Code, ct);
            return CablePath.FromPort(port, graph, links, outletCodes);
        }
        return null;
    }
}

using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Network;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Network;

/// <summary>
/// Escrituras del data center (sección 11.2). Toda referencia (sucursal, sala, gabinete, modelo, puerto, roseta) se
/// valida dentro de la empresa y del alcance de sucursales del usuario; cada cambio queda en auditoría.
/// Las claves SNMP se cifran con la llave de la aplicación (AES-GCM) y nunca se devuelven ni se auditan.
/// </summary>
public sealed class NetworkCommands(MonitoreoDbContext db, NetworkQueries queries, IAuditTrail audit, SecretProtector protector)
{
    private async Task<bool> CanUseBranchAsync(int branchId, CancellationToken ct)
    {
        var allowed = await queries.AllowedBranchesAsync(ct);
        return (allowed is null || allowed.Contains(branchId)) && await db.Branches.AnyAsync(b => b.Id == branchId, ct);
    }

    private static T Parse<T>(string value) where T : struct, Enum => EnumMemberConverter<T>.FromProvider(value);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ------------------------------------------------------------------ salas

    public async Task<CommandResult> SaveRoomAsync(int? id, RoomInput input, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(input.SucursalId, ct))
            return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        var name = input.Nombre.Trim();

        Room? room = null;
        if (id is int roomId && (room = await (await queries.VisibleRoomsAsync(ct)).FirstOrDefaultAsync(r => r.Id == roomId, ct)) is null)
            return CommandResult.NotFound();
        if (await db.Rooms.AnyAsync(r => r.BranchId == input.SucursalId && r.Name == name && r.Id != (id ?? 0), ct))
            return CommandResult.Conflict("Ya hay una sala con ese nombre en la sucursal.");
        if (room is not null && room.BranchId != input.SucursalId && await db.Racks.AnyAsync(r => r.RoomId == room.Id, ct))
            return CommandResult.Invalid("La sala tiene gabinetes: no se puede pasar a otra sucursal.");

        var before = room is null ? null : new { nombre = room.Name, ubicacion = room.Location, sucursalId = room.BranchId };
        room ??= db.Rooms.Add(new Room()).Entity;
        room.Name = name;
        room.Location = Clean(input.Ubicacion);
        room.BranchId = input.SucursalId;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(before is null ? "sala.crear" : "sala.editar", "salas", room.Id.ToString(), before,
            new { nombre = room.Name, ubicacion = room.Location, sucursalId = room.BranchId }, ct);
        return CommandResult.Ok(room.Id);
    }

    public async Task<CommandResult> DeleteRoomAsync(int id, CancellationToken ct)
    {
        var room = await (await queries.VisibleRoomsAsync(ct)).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (room is null)
            return CommandResult.NotFound();
        if (await db.Racks.AnyAsync(r => r.RoomId == id, ct))
            return CommandResult.Conflict("La sala tiene gabinetes: muévalos o elimínelos primero.");
        db.Rooms.Remove(room);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("sala.eliminar", "salas", id.ToString(), new { nombre = room.Name }, null, ct);
        return CommandResult.Ok(id);
    }

    // ------------------------------------------------------------------ gabinetes

    public async Task<CommandResult> SaveRackAsync(int? id, RackInput input, CancellationToken ct)
    {
        var room = await (await queries.VisibleRoomsAsync(ct)).FirstOrDefaultAsync(r => r.Id == input.SalaId, ct);
        if (room is null)
            return CommandResult.Invalid("La sala no existe o no tiene acceso a ella.");

        Rack? rack = null;
        if (id is int rackId && (rack = await (await queries.VisibleRacksAsync(ct)).FirstOrDefaultAsync(r => r.Id == rackId, ct)) is null)
            return CommandResult.NotFound();
        var name = input.Nombre.Trim();
        if (await db.Racks.AnyAsync(r => r.RoomId == input.SalaId && r.Name == name && r.Id != (id ?? 0), ct))
            return CommandResult.Conflict("Ya hay un gabinete con ese nombre en la sala.");

        if (rack is not null)
        {
            var devices = await db.NetworkDevices.Where(d => d.RackId == rack.Id && d.RackPositionU != null).ToListAsync(ct);
            var top = devices.Select(d => d.RackPositionU!.Value + d.HeightUnits - 1).DefaultIfEmpty(0).Max();
            if (top > input.AlturaU)
                return CommandResult.Invalid($"Hay equipos montados hasta la U {top}: el gabinete no puede tener menos U.");
            if (rack.RoomId != input.SalaId && devices.Count > 0 && room.BranchId != devices[0].BranchId)
                return CommandResult.Invalid("El gabinete tiene equipos: solo puede moverse a una sala de la misma sucursal.");
        }

        var before = rack is null ? null : RackSnapshot(rack);
        rack ??= db.Racks.Add(new Rack()).Entity;
        rack.Name = name;
        rack.RoomId = input.SalaId;
        rack.HeightUnits = (byte)input.AlturaU;
        rack.Row = Clean(input.Fila);
        rack.Position = input.Posicion;
        rack.IsActive = input.Activo;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(before is null ? "gabinete.crear" : "gabinete.editar", "gabinetes", rack.Id.ToString(), before, RackSnapshot(rack), ct);
        return CommandResult.Ok(rack.Id);
    }

    public async Task<CommandResult> DeleteRackAsync(int id, CancellationToken ct)
    {
        var rack = await (await queries.VisibleRacksAsync(ct)).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rack is null)
            return CommandResult.NotFound();
        if (await db.NetworkDevices.AnyAsync(d => d.RackId == id, ct))
            return CommandResult.Conflict("El gabinete tiene equipos: muévalos primero.");
        db.Racks.Remove(rack);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("gabinete.eliminar", "gabinetes", id.ToString(), RackSnapshot(rack), null, ct);
        return CommandResult.Ok(id);
    }

    private static object RackSnapshot(Rack r) => new { nombre = r.Name, salaId = r.RoomId, alturaU = r.HeightUnits, fila = r.Row, posicion = r.Position, activo = r.IsActive };

    // ------------------------------------------------------------------ catálogo

    /// <summary>Los modelos propios de la empresa se crean y editan; el catálogo base es de solo lectura.</summary>
    public async Task<CommandResult> SaveModelAsync(int? id, DeviceModelInput input, CancellationToken ct)
    {
        DeviceModel? model = null;
        if (id is int modelId)
        {
            model = await db.DeviceModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
            if (model is null)
                return CommandResult.NotFound();
            if (model.CompanyId is null)
                return CommandResult.Invalid("Los modelos del catálogo base no se editan: cree uno propio.");
            if (model.PortCount != input.CantidadPuertos && await db.NetworkDevices.AnyAsync(d => d.ModelId == modelId, ct))
                return CommandResult.Invalid("Hay dispositivos de este modelo: la cantidad de puertos ya no se puede cambiar.");
        }
        var brand = input.Marca.Trim();
        var name = input.Modelo.Trim();
        if (await db.DeviceModels.AnyAsync(m => m.CompanyId != null && m.Brand == brand && m.ModelName == name && m.Id != (id ?? 0), ct))
            return CommandResult.Conflict("Ese modelo ya está en el catálogo de la empresa.");

        var before = model is null ? null : ModelSnapshot(model);
        model ??= db.DeviceModels.Add(new DeviceModel()).Entity;
        model.Brand = brand;
        model.ModelName = name;
        model.Type = Parse<DeviceType>(input.Tipo);
        model.HeightUnits = (byte)input.AlturaU;
        model.PortCount = input.CantidadPuertos;
        model.PortDescription = Clean(input.TipoPuertos);
        model.IsActiveDevice = input.EsActivo;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(before is null ? "modelo.crear" : "modelo.editar", "modelos_dispositivo", model.Id.ToString(), before, ModelSnapshot(model), ct);
        return CommandResult.Ok(model.Id);
    }

    private static object ModelSnapshot(DeviceModel m) => new
    {
        marca = m.Brand, modelo = m.ModelName, tipo = Text(m.Type), alturaU = m.HeightUnits, puertos = m.PortCount, tipoPuertos = m.PortDescription, activo = m.IsActiveDevice,
    };

    // ------------------------------------------------------------------ dispositivos

    /// <summary>Alta: los puertos se crean según el modelo (cantidad y tipos).</summary>
    public async Task<CommandResult> CreateDeviceAsync(DeviceInput input, CancellationToken ct)
    {
        var model = await db.DeviceModels.FirstOrDefaultAsync(m => m.Id == input.ModeloId, ct);
        if (model is null)
            return CommandResult.Invalid("El modelo no existe.");
        if (await ValidatePlacementAsync(input.SucursalId, input.GabineteId, input.PosicionU, model.HeightUnits, null, ct) is { } invalid)
            return invalid;
        var name = input.Nombre.Trim();
        if (await db.NetworkDevices.AnyAsync(d => d.Name == name, ct))
            return CommandResult.Conflict("Ya hay un dispositivo con ese nombre.");
        if (!model.IsActiveDevice && input.SnmpClaveAutenticacion is not null)
            return CommandResult.Invalid("Un equipo pasivo (patch panel) no tiene SNMP.");

        var device = new NetworkDevice
        {
            Name = name,
            ModelId = model.Id,
            Type = model.Type,
            HeightUnits = model.HeightUnits,
            BranchId = input.SucursalId,
            RackId = input.GabineteId,
            RackPositionU = input.GabineteId is null ? null : (byte?)input.PosicionU,
            ManagementIp = Clean(input.IpGestion),
            Firmware = Clean(input.Firmware),
            Status = model.IsActiveDevice ? NetworkDeviceStatus.NotResponding : NetworkDeviceStatus.Passive,
        };
        ApplySnmp(device, input);
        foreach (var port in PortPlan.FromModel(model.PortCount, model.PortDescription, model.Type == DeviceType.PatchPanel))
            device.Ports.Add(new Port { Number = port.Number, Name = port.Name, Type = Parse<PortType>(port.Type) });

        db.NetworkDevices.Add(device);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("dispositivo.crear", "dispositivos_red", device.Id.ToString(), null, DeviceSnapshot(device), ct);
        return CommandResult.Ok(device.Id);
    }

    public async Task<CommandResult> UpdateDeviceAsync(int id, DeviceInput input, CancellationToken ct)
    {
        var device = await (await queries.VisibleDevicesAsync(ct)).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
            return CommandResult.NotFound();
        if (input.ModeloId != device.ModelId)
            return CommandResult.Invalid("El modelo no se cambia: dé de baja el equipo y registre el nuevo.");
        if (await ValidatePlacementAsync(input.SucursalId, input.GabineteId, input.PosicionU, device.HeightUnits, device.Id, ct) is { } invalid)
            return invalid;
        var name = input.Nombre.Trim();
        if (await db.NetworkDevices.AnyAsync(d => d.Name == name && d.Id != id, ct))
            return CommandResult.Conflict("Ya hay un dispositivo con ese nombre.");
        if (device.Status == NetworkDeviceStatus.Passive && input.SnmpClaveAutenticacion is not null)
            return CommandResult.Invalid("Un equipo pasivo (patch panel) no tiene SNMP.");

        var before = DeviceSnapshot(device);
        device.Name = name;
        device.BranchId = input.SucursalId;
        device.RackId = input.GabineteId;
        device.RackPositionU = input.GabineteId is null ? null : (byte?)input.PosicionU;
        device.ManagementIp = Clean(input.IpGestion);
        device.Firmware = Clean(input.Firmware);
        ApplySnmp(device, input);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("dispositivo.editar", "dispositivos_red", id.ToString(), before, DeviceSnapshot(device), ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Editor de gabinete (W-60): arrastrar a otra U, a otro gabinete o sacarlo del gabinete.</summary>
    public async Task<CommandResult> PlaceDeviceAsync(int id, DevicePlacementInput input, CancellationToken ct)
    {
        var device = await (await queries.VisibleDevicesAsync(ct)).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
            return CommandResult.NotFound();
        if (input.GabineteId is not null && input.PosicionU is null)
            return CommandResult.Invalid("Indique la U donde va en el gabinete.");
        if (await ValidatePlacementAsync(device.BranchId, input.GabineteId, input.PosicionU, device.HeightUnits, device.Id, ct) is { } invalid)
            return invalid;

        var before = new { gabineteId = device.RackId, posicionU = device.RackPositionU };
        device.RackId = input.GabineteId;
        device.RackPositionU = input.GabineteId is null ? null : (byte?)input.PosicionU;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("dispositivo.mover", "dispositivos_red", id.ToString(), before, new { gabineteId = device.RackId, posicionU = device.RackPositionU }, ct);
        return CommandResult.Ok(id);
    }

    /// <summary>Dar de baja: no se borra (conserva su historial), libera su lugar en el gabinete y deja de consultarse.</summary>
    public async Task<CommandResult> RetireDeviceAsync(int id, CancellationToken ct)
    {
        var device = await (await queries.VisibleDevicesAsync(ct)).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
            return CommandResult.NotFound();
        var before = DeviceSnapshot(device);
        device.Status = NetworkDeviceStatus.Retired;
        device.RackId = null;
        device.RackPositionU = null;
        device.CollectorAgentId = null;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("dispositivo.baja", "dispositivos_red", id.ToString(), before, DeviceSnapshot(device), ct);
        return CommandResult.Ok(id);
    }

    /// <summary>La sucursal debe ser visible; si va en gabinete, el gabinete es de esa sucursal y las U están libres.</summary>
    private async Task<CommandResult?> ValidatePlacementAsync(int branchId, int? rackId, int? positionU, int heightUnits, int? deviceId, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(branchId, ct))
            return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        if (rackId is not int id)
            return null;

        var rack = await (await queries.VisibleRacksAsync(ct)).Select(r => new { r.Id, r.HeightUnits, r.Room!.BranchId }).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rack is null)
            return CommandResult.Invalid("El gabinete no existe o no tiene acceso a él.");
        if (rack.BranchId != branchId)
            return CommandResult.Invalid("El gabinete es de otra sucursal.");

        var placed = await db.NetworkDevices.Where(d => d.RackId == id && d.RackPositionU != null)
            .Select(d => new RackSlot(d.Id, d.Name, d.RackPositionU!.Value, d.HeightUnits)).ToListAsync(ct);
        return RackLayout.Check(rack.HeightUnits, placed, positionU ?? 0, heightUnits, deviceId) is { } reason ? CommandResult.Invalid(reason) : null;
    }

    private void ApplySnmp(NetworkDevice device, DeviceInput input)
    {
        device.SnmpUser = Clean(input.SnmpUsuario);
        if (device.SnmpUser is null)
        {
            device.SnmpEncryptedCredential = null;
            return;
        }
        if (input.SnmpClaveAutenticacion is not null && input.SnmpClavePrivacidad is not null)
        {
            var secret = JsonSerializer.Serialize(new { auth = input.SnmpClaveAutenticacion, priv = input.SnmpClavePrivacidad });
            device.SnmpEncryptedCredential = Encoding.UTF8.GetBytes(protector.Protect(secret));
        }
    }

    /// <summary>Lo que se audita del dispositivo: nunca las claves SNMP, solo si hay credencial.</summary>
    private static object DeviceSnapshot(NetworkDevice d) => new
    {
        nombre = d.Name, sucursalId = d.BranchId, gabineteId = d.RackId, posicionU = d.RackPositionU, ip = d.ManagementIp,
        firmware = d.Firmware, estado = Text(d.Status), snmpUsuario = d.SnmpUser, snmpCredencial = d.SnmpEncryptedCredential is not null,
    };

    // ------------------------------------------------------------------ puertos

    public async Task<CommandResult> UpdatePortAsync(int id, PortInput input, CancellationToken ct)
    {
        var devices = (await queries.VisibleDevicesAsync(ct)).Select(d => d.Id);
        var port = await db.Ports.FirstOrDefaultAsync(p => p.Id == id && devices.Contains(p.DeviceId), ct);
        if (port is null)
            return CommandResult.NotFound();
        var before = new { nombre = port.Name, tipo = Text(port.Type), vlan = port.Vlan, hostVm = port.IsVmHost };
        port.Name = Clean(input.Nombre);
        port.Type = Parse<PortType>(input.Tipo);
        port.Vlan = input.Vlan;
        port.IsVmHost = input.EsHostVm;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("puerto.editar", "puertos", id.ToString(), before, new { nombre = port.Name, tipo = input.Tipo, vlan = port.Vlan, hostVm = port.IsVmHost }, ct);
        return CommandResult.Ok(id);
    }

    // ------------------------------------------------------------------ rosetas

    public async Task<CommandResult> SaveOutletAsync(int? id, OutletInput input, CancellationToken ct)
    {
        if (!await CanUseBranchAsync(input.SucursalId, ct))
            return CommandResult.Invalid("La sucursal no existe o no tiene acceso a ella.");
        NetworkOutlet? outlet = null;
        if (id is int outletId && (outlet = await (await queries.VisibleOutletsAsync(ct)).FirstOrDefaultAsync(o => o.Id == outletId, ct)) is null)
            return CommandResult.NotFound();
        var code = input.Codigo.Trim();
        if (await db.NetworkOutlets.AnyAsync(o => o.BranchId == input.SucursalId && o.Code == code && o.Id != (id ?? 0), ct))
            return CommandResult.Conflict("Ya hay una roseta con ese código en la sucursal.");
        if (outlet is not null && outlet.BranchId != input.SucursalId && await db.Cables.AnyAsync(c => c.OutletId == outlet.Id, ct))
            return CommandResult.Invalid("La roseta tiene un cable: quítelo antes de pasarla a otra sucursal.");

        var before = outlet is null ? null : new { codigo = outlet.Code, ubicacion = outlet.Location, sucursalId = outlet.BranchId };
        outlet ??= db.NetworkOutlets.Add(new NetworkOutlet()).Entity;
        outlet.Code = code;
        outlet.Location = Clean(input.Ubicacion);
        outlet.BranchId = input.SucursalId;
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(before is null ? "roseta.crear" : "roseta.editar", "puntos_red", outlet.Id.ToString(), before,
            new { codigo = outlet.Code, ubicacion = outlet.Location, sucursalId = outlet.BranchId }, ct);
        return CommandResult.Ok(outlet.Id);
    }

    public async Task<CommandResult> DeleteOutletAsync(int id, CancellationToken ct)
    {
        var outlet = await (await queries.VisibleOutletsAsync(ct)).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (outlet is null)
            return CommandResult.NotFound();
        if (await db.Cables.AnyAsync(c => c.OutletId == id, ct))
            return CommandResult.Conflict("La roseta tiene un cable: quítelo primero.");
        db.NetworkOutlets.Remove(outlet);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("roseta.eliminar", "puntos_red", id.ToString(), new { codigo = outlet.Code }, null, ct);
        return CommandResult.Ok(id);
    }

    // ------------------------------------------------------------------ cableado

    /// <summary>
    /// Un cable une dos puertos o un puerto con una roseta. Un puerto de equipo activo admite un cable; uno de patch
    /// panel, dos (atrás a la roseta y adelante al switch); una roseta, uno. Todo en la misma sucursal.
    /// </summary>
    public async Task<CommandResult> CreateCableAsync(CableInput input, CancellationToken ct)
    {
        var devices = (await queries.VisibleDevicesAsync(ct)).Select(d => d.Id);
        var ids = new[] { input.PuertoAId, input.PuertoBId ?? 0 };
        var ports = await db.Ports.Where(p => ids.Contains(p.Id) && devices.Contains(p.DeviceId))
            .Select(p => new { p.Id, p.DeviceId, Label = p.Device!.Name + " " + (p.Name ?? p.Number.ToString()), p.Device.BranchId, Passive = !p.Device.Model!.IsActiveDevice })
            .ToListAsync(ct);
        var a = ports.FirstOrDefault(p => p.Id == input.PuertoAId);
        var b = ports.FirstOrDefault(p => p.Id == input.PuertoBId);
        if (a is null || (input.PuertoBId is not null && b is null))
            return CommandResult.Invalid("El puerto no existe o no tiene acceso a él.");
        if (b is not null && b.DeviceId == a.DeviceId)
            return CommandResult.Invalid("Un cable no une dos puertos del mismo dispositivo.");
        if (b is not null && b.BranchId != a.BranchId)
            return CommandResult.Invalid("Los dos puertos deben estar en la misma sucursal.");

        NetworkOutlet? outlet = null;
        if (input.PuntoRedId is int outletId)
        {
            outlet = await (await queries.VisibleOutletsAsync(ct)).FirstOrDefaultAsync(o => o.Id == outletId, ct);
            if (outlet is null)
                return CommandResult.Invalid("La roseta no existe o no tiene acceso a ella.");
            if (outlet.BranchId != a.BranchId)
                return CommandResult.Invalid("La roseta es de otra sucursal.");
            if (await db.Cables.AnyAsync(c => c.OutletId == outletId, ct))
                return CommandResult.Conflict($"La roseta {outlet.Code} ya tiene un cable.");
        }

        foreach (var port in new[] { a, b })
        {
            if (port is null)
                continue;
            var used = await db.Cables.Where(c => c.PortAId == port.Id || c.PortBId == port.Id)
                .Select(c => new { ToOutlet = c.OutletId != null }).ToListAsync(ct);
            if (used.Count >= (port.Passive ? 2 : 1))
                return CommandResult.Conflict($"{port.Label} ya tiene {(port.Passive ? "sus dos cables" : "un cable")}.");
            if (port.Passive && outlet is not null && used.Any(u => u.ToOutlet))
                return CommandResult.Conflict($"{port.Label} ya llega a una roseta.");
        }

        var cable = new Cable
        {
            PortAId = a.Id,
            PortBId = b?.Id,
            OutletId = outlet?.Id,
            CableType = input.TipoCable is null ? null : Parse<CableType>(input.TipoCable),
            Color = Clean(input.Color),
            Label = Clean(input.Etiqueta),
            Notes = Clean(input.Notas),
        };
        db.Cables.Add(cable);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("cable.crear", "cableado", cable.Id.ToString(), null,
            new { desde = a.Label, hasta = b?.Label ?? $"Roseta {outlet!.Code}", etiqueta = cable.Label }, ct);
        return CommandResult.Ok(cable.Id);
    }

    public async Task<CommandResult> DeleteCableAsync(int id, CancellationToken ct)
    {
        var devices = (await queries.VisibleDevicesAsync(ct, includeRetired: true)).Select(d => d.Id);
        var cable = await db.Cables.FirstOrDefaultAsync(c => c.Id == id && db.Ports.Any(p => p.Id == c.PortAId && devices.Contains(p.DeviceId)), ct);
        if (cable is null)
            return CommandResult.NotFound();
        db.Cables.Remove(cable);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("cable.eliminar", "cableado", id.ToString(),
            new { puertoA = cable.PortAId, puertoB = cable.PortBId, rosetaId = cable.OutletId, etiqueta = cable.Label }, null, ct);
        return CommandResult.Ok(id);
    }
}

using FluentValidation;

namespace MonitoreoTI.Application.Network;

// ------------------------------------------------------------------ salas y gabinetes (W-50, W-58)

public sealed record RoomItem(int Id, string Name, string? Location, int BranchId, string BranchName, int RackCount, int DeviceCount);

public sealed record RoomInput(string Nombre, string? Ubicacion, int SucursalId);

public sealed record RackItem(
    int Id, string Name, int RoomId, string RoomName, int BranchId, string BranchName, int HeightUnits, int UsedUnits,
    string? Row, int? Position, bool IsActive, int DeviceCount, Guid QrCode);

/// <summary>Equipo montado, para dibujar el gabinete en 2D (W-45) y moverlo en el editor (W-60).</summary>
public sealed record RackDevice(
    int Id, string Name, string Type, string ModelName, int PositionU, int HeightUnits, string Status,
    int PortCount, int PortsConnected, int PortsWithProblems);

public sealed record RackDetail(RackItem Rack, IReadOnlyList<RackDevice> Devices);

public sealed record RackInput(string Nombre, int SalaId, int AlturaU, string? Fila, int? Posicion, bool Activo = true);

// ------------------------------------------------------------------ catálogo de modelos (W-56, W-57)

public sealed record DeviceModelItem(
    int Id, string Brand, string Model, string Type, int HeightUnits, int PortCount, string? PortDescription,
    bool IsActiveDevice,
    /// <summary>Del catálogo base (compartido, solo lectura) o propio de la empresa.</summary>
    bool IsShared);

public sealed record DeviceModelInput(string Marca, string Modelo, string Tipo, int AlturaU, int CantidadPuertos, string? TipoPuertos, bool EsActivo);

// ------------------------------------------------------------------ dispositivos (W-46, W-48, W-49, W-61)

public sealed record DeviceFilter(string? Query = null, string? Type = null, int? BranchId = null, int? RoomId = null, int? RackId = null);

public sealed record DeviceListItem(
    int Id, string Name, string Type, string Brand, string ModelName, int BranchId, string BranchName,
    int? RackId, string? RackName, string? RoomName, int? PositionU, int HeightUnits, string? ManagementIp,
    string Status, DateTime? LastPolledAt, int PortCount, int PortsConnected, int PortsWithProblems);

public sealed record PortItem(
    int Id, int Number, string? Name, string Type, int? Vlan, int? SpeedMbps, string Status,
    bool IsTrunk, bool IsVmHost, bool IsManualAssignment,
    /// <summary>Cable documentado y a qué llega ("PP-A01-01 P12", "Roseta P-2-15").</summary>
    int? CableId, string? CableTo,
    /// <summary>Último equipo con agente visto en el puerto (tabla MAC, Fase 4.3).</summary>
    int? MachineId, string? MachineName);

public sealed record DeviceDetail(
    int Id, string Name, string Type, int ModelId, string Brand, string ModelName, bool IsActiveDevice,
    int BranchId, string BranchName, int? RackId, string? RackName, int? RoomId, string? RoomName,
    int? PositionU, int HeightUnits, string? ManagementIp, string? Firmware, string Status, DateTime? LastPolledAt,
    string? SnmpUser, bool HasSnmpCredential, Guid QrCode, DateTime CreatedAt, IReadOnlyList<PortItem> Ports);

/// <summary>
/// Alta o edición. Las claves SNMPv3 (autenticación SHA y privacidad AES) solo se envían para cambiarlas:
/// null conserva las guardadas. Nunca se devuelven.
/// </summary>
public sealed record DeviceInput(
    string Nombre, int ModeloId, int SucursalId, int? GabineteId, int? PosicionU, string? IpGestion, string? Firmware,
    string? SnmpUsuario, string? SnmpClaveAutenticacion, string? SnmpClavePrivacidad);

/// <summary>Mover en el editor de gabinete: otro gabinete, otra U o fuera de gabinete (ambos null).</summary>
public sealed record DevicePlacementInput(int? GabineteId, int? PosicionU);

public sealed record PortInput(string? Nombre, string Tipo, int? Vlan, bool EsHostVm);

// ------------------------------------------------------------------ rosetas y cableado (W-62 a W-65)

public sealed record OutletItem(int Id, string Code, string? Location, int BranchId, string BranchName, int? CableId, int? PortId, string? PortLabel, Guid QrCode);

public sealed record OutletInput(string Codigo, string? Ubicacion, int SucursalId);

public sealed record CableItem(
    int Id, int PortAId, string PortALabel, int? PortBId, string? PortBLabel, int? OutletId, string? OutletCode,
    string? Type, string? Color, string? Label, string? Notes, DateTime CreatedAt);

public sealed record CableInput(int PuertoAId, int? PuertoBId, int? PuntoRedId, string? TipoCable, string? Color, string? Etiqueta, string? Notas);

public static class NetworkValues
{
    public static readonly string[] DeviceTypes =
        ["switch", "router", "firewall", "servidor", "patch_panel", "ups", "pdu", "bandeja", "organizador", "access_point", "otro"];
    public static readonly string[] PortTypes = ["rj45", "sfp", "sfp_plus", "fibra", "consola", "otro"];
    public static readonly string[] CableTypes = ["cat5e", "cat6", "cat6a", "cat7", "fibra_mm", "fibra_sm", "dac", "otro"];
}

// ------------------------------------------------------------------ validaciones

public sealed class RoomInputValidator : AbstractValidator<RoomInput>
{
    public RoomInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre de la sala.").MaximumLength(100);
        RuleFor(x => x.Ubicacion).MaximumLength(200);
        RuleFor(x => x.SucursalId).GreaterThan(0).WithMessage("Elija la sucursal.");
    }
}

public sealed class RackInputValidator : AbstractValidator<RackInput>
{
    public RackInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre del gabinete.").MaximumLength(50);
        RuleFor(x => x.SalaId).GreaterThan(0).WithMessage("Elija la sala.");
        RuleFor(x => x.AlturaU).InclusiveBetween(1, 60).WithMessage("La altura va de 1 a 60 U.");
        RuleFor(x => x.Fila).MaximumLength(10);
        RuleFor(x => x.Posicion).InclusiveBetween(1, 1000).When(x => x.Posicion is not null);
    }
}

public sealed class DeviceModelInputValidator : AbstractValidator<DeviceModelInput>
{
    public DeviceModelInputValidator()
    {
        RuleFor(x => x.Marca).NotEmpty().WithMessage("Escriba la marca.").MaximumLength(100);
        RuleFor(x => x.Modelo).NotEmpty().WithMessage("Escriba el modelo.").MaximumLength(100);
        RuleFor(x => x.Tipo).Must(t => NetworkValues.DeviceTypes.Contains(t)).WithMessage("Tipo no válido.");
        RuleFor(x => x.AlturaU).InclusiveBetween(0, 20).WithMessage("La altura va de 0 a 20 U.");
        RuleFor(x => x.CantidadPuertos).InclusiveBetween(0, 512).WithMessage("La cantidad de puertos va de 0 a 512.");
        RuleFor(x => x.TipoPuertos).MaximumLength(100);
    }
}

public sealed class DeviceInputValidator : AbstractValidator<DeviceInput>
{
    public DeviceInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre del dispositivo.").MaximumLength(100);
        RuleFor(x => x.ModeloId).GreaterThan(0).WithMessage("Elija el modelo.");
        RuleFor(x => x.SucursalId).GreaterThan(0).WithMessage("Elija la sucursal.");
        RuleFor(x => x.PosicionU).NotNull().When(x => x.GabineteId is not null).WithMessage("Indique la U donde va en el gabinete.");
        RuleFor(x => x.PosicionU).InclusiveBetween(1, 60).When(x => x.PosicionU is not null);
        RuleFor(x => x.IpGestion).Must(ip => ip is null || System.Net.IPAddress.TryParse(ip, out _)).WithMessage("La IP no es válida.");
        RuleFor(x => x.Firmware).MaximumLength(100);
        RuleFor(x => x.SnmpUsuario).MaximumLength(100);
        // SNMPv3 exige al menos 8 caracteres en cada clave.
        RuleFor(x => x.SnmpClaveAutenticacion).MinimumLength(8).MaximumLength(64).When(x => x.SnmpClaveAutenticacion is not null)
            .WithMessage("La clave de autenticación SNMPv3 debe tener entre 8 y 64 caracteres.");
        RuleFor(x => x.SnmpClavePrivacidad).MinimumLength(8).MaximumLength(64).When(x => x.SnmpClavePrivacidad is not null)
            .WithMessage("La clave de privacidad SNMPv3 debe tener entre 8 y 64 caracteres.");
        RuleFor(x => x.SnmpUsuario).NotEmpty().When(x => x.SnmpClaveAutenticacion is not null || x.SnmpClavePrivacidad is not null)
            .WithMessage("Indique el usuario SNMPv3.");
        RuleFor(x => x).Must(x => (x.SnmpClaveAutenticacion is null) == (x.SnmpClavePrivacidad is null))
            .OverridePropertyName("SnmpClavePrivacidad").WithMessage("SNMPv3 authPriv necesita las dos claves.");
    }
}

public sealed class PortInputValidator : AbstractValidator<PortInput>
{
    public PortInputValidator()
    {
        RuleFor(x => x.Nombre).MaximumLength(50);
        RuleFor(x => x.Tipo).Must(t => NetworkValues.PortTypes.Contains(t)).WithMessage("Tipo de puerto no válido.");
        RuleFor(x => x.Vlan).InclusiveBetween(1, 4094).When(x => x.Vlan is not null).WithMessage("La VLAN va de 1 a 4094.");
    }
}

public sealed class OutletInputValidator : AbstractValidator<OutletInput>
{
    public OutletInputValidator()
    {
        RuleFor(x => x.Codigo).NotEmpty().WithMessage("Escriba el código de la roseta.").MaximumLength(30);
        RuleFor(x => x.Ubicacion).MaximumLength(200);
        RuleFor(x => x.SucursalId).GreaterThan(0).WithMessage("Elija la sucursal.");
    }
}

public sealed class CableInputValidator : AbstractValidator<CableInput>
{
    public CableInputValidator()
    {
        RuleFor(x => x.PuertoAId).GreaterThan(0).WithMessage("Elija el puerto de origen.");
        RuleFor(x => x).Must(x => (x.PuertoBId is null) != (x.PuntoRedId is null))
            .OverridePropertyName("PuertoBId").WithMessage("El cable llega a otro puerto o a una roseta (uno de los dos).");
        RuleFor(x => x.PuertoBId).NotEqual(x => x.PuertoAId).When(x => x.PuertoBId is not null)
            .WithMessage("Un cable no puede unir un puerto consigo mismo.");
        RuleFor(x => x.TipoCable).Must(t => t is null || NetworkValues.CableTypes.Contains(t)).WithMessage("Tipo de cable no válido.");
        RuleFor(x => x.Color).MaximumLength(30);
        RuleFor(x => x.Etiqueta).MaximumLength(50);
        RuleFor(x => x.Notas).MaximumLength(500);
    }
}

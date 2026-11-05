using FluentValidation;

namespace MonitoreoTI.Application.Panel;

/// <summary>Resultado de una escritura: el id creado o modificado, o el motivo del rechazo.</summary>
public sealed record CommandResult(long? Id, CommandError? Error, string? Message = null)
{
    public bool Succeeded => Error is null;
    public static CommandResult Ok(long id) => new(id, null);
    public static CommandResult NotFound() => new(null, CommandError.NotFound);
    public static CommandResult Conflict(string message) => new(null, CommandError.Conflict, message);
    public static CommandResult Invalid(string message) => new(null, CommandError.Invalid, message);
}

public enum CommandError
{
    NotFound,
    Conflict,
    Invalid,
}

// ------------------------------------------------------------------ equipos (W-13)

/// <summary>Alta o edición manual de un equipo (impresoras, equipos sin agente, datos de compra y garantía).</summary>
public sealed record MachineInput(
    string Nombre,
    string Tipo,
    int SucursalId,
    int? DepartamentoId,
    string? Marca,
    string? Modelo,
    string? Serie,
    string? SistemaOperativo,
    string? Ip,
    string? Mac,
    string? Responsable,
    DateOnly? FechaCompra,
    DateOnly? GarantiaHasta,
    /// <summary>
    /// Impresoras: v2c o v3 (null = sin SNMP). La comunidad o las claves solo se envían para cambiarlas (null las conserva);
    /// nunca se devuelven. El recolector es un agente de la misma sucursal.
    /// </summary>
    string? SnmpVersion = null,
    string? SnmpUsuario = null,
    string? SnmpComunidad = null,
    string? SnmpClaveAutenticacion = null,
    string? SnmpClavePrivacidad = null,
    int? RecolectorId = null);

public sealed class MachineInputValidator : AbstractValidator<MachineInput>
{
    private static readonly string[] Types = ["computadora", "laptop", "servidor", "impresora", "otro"];

    public MachineInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre del equipo.").MaximumLength(100);
        RuleFor(x => x.Tipo).Must(t => Types.Contains(t)).WithMessage("Tipo de equipo no válido.");
        RuleFor(x => x.SucursalId).GreaterThan(0).WithMessage("Elija la sucursal.");
        RuleFor(x => x.Marca).MaximumLength(100);
        RuleFor(x => x.Modelo).MaximumLength(100);
        RuleFor(x => x.Serie).MaximumLength(100);
        RuleFor(x => x.SistemaOperativo).MaximumLength(150);
        RuleFor(x => x.Responsable).MaximumLength(150);
        RuleFor(x => x.Ip).Must(ip => ip is null || System.Net.IPAddress.TryParse(ip, out _))
            .WithMessage("La IP no es válida.").MaximumLength(45);
        RuleFor(x => x.Mac).Must(mac => mac is null || Agents.MacAddress.TryNormalize(mac, out _))
            .WithMessage("La MAC debe tener el formato AA:BB:CC:DD:EE:FF.");
        RuleFor(x => x.SnmpVersion).Must(v => v is null or "v2c" or "v3").WithMessage("La versión SNMP es v2c o v3.");
        RuleFor(x => x.SnmpVersion).Null().When(x => x.Tipo != "impresora").WithMessage("Solo las impresoras se consultan por SNMP desde su ficha.");
        RuleFor(x => x.SnmpComunidad).MaximumLength(64).Must(c => c is null || c.Trim().Length > 0).WithMessage("Escriba la comunidad SNMP.");
        RuleFor(x => x.SnmpUsuario).NotEmpty().When(x => x.SnmpVersion == "v3").WithMessage("Indique el usuario SNMPv3.").MaximumLength(100);
        RuleFor(x => x.SnmpClaveAutenticacion).MinimumLength(8).MaximumLength(64).When(x => x.SnmpClaveAutenticacion is not null)
            .WithMessage("La clave de autenticación SNMPv3 debe tener entre 8 y 64 caracteres.");
        RuleFor(x => x.SnmpClavePrivacidad).MinimumLength(8).MaximumLength(64).When(x => x.SnmpClavePrivacidad is not null)
            .WithMessage("La clave de privacidad SNMPv3 debe tener entre 8 y 64 caracteres.");
        RuleFor(x => x).Must(x => (x.SnmpClaveAutenticacion is null) == (x.SnmpClavePrivacidad is null))
            .OverridePropertyName("SnmpClavePrivacidad").WithMessage("SNMPv3 authPriv necesita las dos claves.");
        RuleFor(x => x.Ip).NotEmpty().When(x => x.SnmpVersion is not null).WithMessage("La impresora necesita su IP para consultarla por SNMP.");
        RuleFor(x => x.GarantiaHasta).GreaterThanOrEqualTo(x => x.FechaCompra)
            .When(x => x.FechaCompra is not null && x.GarantiaHasta is not null)
            .WithMessage("La garantía no puede vencer antes de la compra.");
    }
}

// ------------------------------------------------------------------ sucursales (W-17)

public sealed record BranchInput(string Nombre, string? Provincia, string? Direccion, int? ResponsableId, bool Activa = true);

public sealed class BranchInputValidator : AbstractValidator<BranchInput>
{
    public BranchInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre de la sucursal.").MaximumLength(100);
        RuleFor(x => x.Provincia).MaximumLength(100);
        RuleFor(x => x.Direccion).MaximumLength(300);
    }
}

// ------------------------------------------------------------------ instaladores (W-18)

public sealed record EnrollmentTokenInput(int SucursalId, string? Nombre, int DiasVigencia, int? UsosMax, bool RequiereAprobacion);

public sealed class EnrollmentTokenInputValidator : AbstractValidator<EnrollmentTokenInput>
{
    public EnrollmentTokenInputValidator()
    {
        RuleFor(x => x.SucursalId).GreaterThan(0).WithMessage("Elija la sucursal.");
        RuleFor(x => x.Nombre).MaximumLength(100);
        RuleFor(x => x.DiasVigencia).InclusiveBetween(1, 365).WithMessage("La vigencia va de 1 a 365 días.");
        RuleFor(x => x.UsosMax).InclusiveBetween(1, 100_000).When(x => x.UsosMax is not null)
            .WithMessage("El límite de equipos va de 1 a 100 000.");
    }
}

public sealed record CreatedEnrollmentToken(int Id, string Token, DateTime ExpiresAt);

public sealed record EnrollmentTokenItem(
    int Id,
    string? Name,
    int BranchId,
    string BranchName,
    DateTime ExpiresAt,
    int? MaxUses,
    int Uses,
    bool RequiresApproval,
    DateTime? RevokedAt,
    DateTime CreatedAt,
    string? CreatedBy,
    /// <summary>activo, vencido, agotado o revocado.</summary>
    string State,
    /// <summary>Se puede volver a ver el comando: está activo y se guardó cifrado (los tokens anteriores no).</summary>
    bool CanShowCommand = false);

public sealed record PendingAgentItem(int AgentId, int MachineId, string MachineName, string? IpAddress, string? MacAddress,
    string? OperatingSystem, string BranchName, DateTime RegisteredAt);

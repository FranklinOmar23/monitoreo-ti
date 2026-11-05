using FluentValidation;

namespace MonitoreoTI.Application.Settings;

// ------------------------------------------------------------------ empresa y plan (W-40)

/// <summary>Datos de la empresa que edita su administrador. El plan lo cambia el proveedor (Fase 8).</summary>
public sealed record CompanyInput(string Nombre, string? Rnc, string? CorreoContacto, string? Telefono);

public sealed class CompanyInputValidator : AbstractValidator<CompanyInput>
{
    public CompanyInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre de la empresa.").MaximumLength(150);
        // RNC (9 dígitos) o cédula (11), sin guiones.
        RuleFor(x => x.Rnc).Matches(@"^(\d{9}|\d{11})$").When(x => !string.IsNullOrEmpty(x.Rnc))
            .WithMessage("El RNC tiene 9 dígitos (o 11 si es una cédula), sin guiones.");
        RuleFor(x => x.CorreoContacto).MaximumLength(200).EmailAddress().When(x => !string.IsNullOrEmpty(x.CorreoContacto))
            .WithMessage("El correo no es válido.");
        RuleFor(x => x.Telefono).MaximumLength(20).Matches(@"^[0-9+()\- ]{7,20}$").When(x => !string.IsNullOrEmpty(x.Telefono))
            .WithMessage("El teléfono solo lleva números, espacios, guiones, paréntesis o +.");
    }
}

/// <summary>Usado frente al máximo del plan; Max null = sin límite.</summary>
public sealed record PlanUsage(int Used, int? Max);

public sealed record CompanySettings(
    string Name,
    string? TaxId,
    string? ContactEmail,
    string? Phone,
    string PlanName,
    PlanUsage Agents,
    PlanUsage Branches,
    PlanUsage Users,
    IReadOnlyList<string> Modules);

// ------------------------------------------------------------------ apariencia

/// <summary>Lo que pinta el panel: el color de la empresa y su logo como data URL (null = los de Centinela).</summary>
public sealed record AppearanceData(string? Color, string? Logo);

public sealed record ColorInput(string? Color);

public sealed class ColorInputValidator : AbstractValidator<ColorInput>
{
    public ColorInputValidator() =>
        RuleFor(x => x.Color).Matches("^#[0-9a-fA-F]{6}$").When(x => x.Color is not null)
            .WithMessage("El color va como #rrggbb.");
}

/// <summary>El logo en base64. El tipo se deduce de los bytes, no de lo que diga el panel.</summary>
public sealed record LogoInput(string Datos);

// ------------------------------------------------------------------ departamentos

/// <summary>Departamento de una sucursal (decisión 79) con sus equipos activos y su responsable.</summary>
public sealed record DepartmentItem(
    int Id, string Name, int MachineCount, int BranchId, string BranchName, int? ResponsibleId, string? ResponsibleName);

/// <summary>La sucursal va en la ruta (/sucursales/{id}/departamentos).</summary>
public sealed record DepartmentInput(string Nombre, int? ResponsableId = null);

public sealed class DepartmentInputValidator : AbstractValidator<DepartmentInput>
{
    public DepartmentInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre del departamento.").MaximumLength(100);
        RuleFor(x => x.ResponsableId).GreaterThan(0).When(x => x.ResponsableId is not null);
    }
}

// ------------------------------------------------------------------ seguridad y correo

/// <summary>Ajustes de la empresa que también están en Acceso remoto e Instaladores.</summary>
public sealed record SecuritySettings(bool PedirPermiso, bool ActualizacionAutomatica);

/// <summary>smtp (se envían), archivo (desarrollo: se guardan como archivo) o sin_configurar. Nunca lleva claves.</summary>
public sealed record EmailStatus(string Mode, string? From, string? Host);

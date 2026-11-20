using FluentValidation;
using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Panel;

/// <summary>
/// Errores del equipo agrupados por origen y código (decisión 91): cuántas veces, cuándo, qué significa, qué marcó el
/// técnico y qué se puede hacer desde Centinela.
/// </summary>
public sealed record ErrorGroupItem(
    string? Source,
    int? Code,
    /// <summary>error o critico (el peor del grupo).</summary>
    string Level,
    int Count,
    DateTime FirstAt,
    DateTime LastAt,
    string? Title,
    string? Detail,
    string? Advice,
    string? LastMessage,
    /// <summary>abierto, resuelto, reaparecio o ignorado.</summary>
    string State,
    /// <summary>equipo o empresa; null si está abierto.</summary>
    string? StateScope,
    string? StateBy,
    DateTime? StateAt,
    string? StateNote,
    /// <summary>Veces que ocurrió después de marcarlo resuelto (o las del rango si está abierto).</summary>
    int OpenCount,
    /// <summary>Servicio que "Reiniciar servicio" reinicia; null si el error no tiene esa acción en este equipo.</summary>
    string? Service,
    /// <summary>Ruido de Windows: la ficha sugiere ignorarlo.</summary>
    bool Noise);

public sealed record ErrorGroupList(int Days, int OpenTotal, IReadOnlyList<ErrorGroupItem> Groups);

/// <summary>Marcar un grupo resuelto o ignorado. Alcance "empresa" solo para ignorar (y solo quien ve todas las sucursales).</summary>
public sealed record ErrorGroupMarkInput(string? Origen, int? Codigo, string Estado, string? Alcance, string? Nota);

/// <summary>Quitar la marca (vuelve a abierto) o pedir la reparación de un grupo.</summary>
public sealed record ErrorGroupKeyInput(string? Origen, int? Codigo, string? Alcance = null);

public sealed class ErrorGroupMarkInputValidator : AbstractValidator<ErrorGroupMarkInput>
{
    public ErrorGroupMarkInputValidator()
    {
        RuleFor(x => x.Origen).MaximumLength(200);
        RuleFor(x => x.Estado).Must(e => e is ErrorGroupRules.Resolved or ErrorGroupRules.Ignored)
            .WithMessage("El estado debe ser resuelto o ignorado.");
        RuleFor(x => x.Alcance).Must(a => a is null or "equipo" or "empresa").WithMessage("El alcance debe ser equipo o empresa.");
        RuleFor(x => x.Alcance).Must(a => a != "empresa").When(x => x.Estado == ErrorGroupRules.Resolved)
            .WithMessage("Resolver vale solo para este equipo; en toda la empresa solo se puede ignorar.");
        RuleFor(x => x.Nota).MaximumLength(500).WithMessage("La nota admite hasta 500 caracteres.");
    }
}

public sealed class ErrorGroupKeyInputValidator : AbstractValidator<ErrorGroupKeyInput>
{
    public ErrorGroupKeyInputValidator()
    {
        RuleFor(x => x.Origen).MaximumLength(200);
        RuleFor(x => x.Alcance).Must(a => a is null or "equipo" or "empresa").WithMessage("El alcance debe ser equipo o empresa.");
    }
}

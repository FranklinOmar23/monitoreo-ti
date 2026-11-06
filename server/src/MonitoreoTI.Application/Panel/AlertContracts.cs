using FluentValidation;
using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Panel;

// ------------------------------------------------------------------ alertas (W-20, W-21)

/// <param name="Status">abiertas (por defecto), nueva, en_proceso, resuelta o todas.</param>
public sealed record AlertFilter
{
    public string? Query { get; init; }
    public string? Status { get; init; }
    public string? Level { get; init; }
    public int? BranchId { get; init; }
    public int? MachineId { get; init; }
    public int? DeviceId { get; init; }

    /// <summary>true = solo las de red (switches, puertos); false = solo las de equipos.</summary>
    public bool? Network { get; init; }
    public string? Source { get; init; }
    public string? Code { get; init; }
    public int? HandledById { get; init; }

    /// <summary>true = las del motor de alertas; false = las creadas a mano.</summary>
    public bool? Automatic { get; init; }

    /// <summary>Fecha en que se creó la alerta.</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int Page { get; init; } = 1;
    public int Limit { get; init; } = Paging.DefaultLimit;
}

/// <summary>Contadores de la cabecera de alertas (sin los filtros de estado y nivel).</summary>
public sealed record AlertCounts(int Open, int New, int InProgress, int Critical, int Warning, int ResolvedLast24h);

public sealed record AlertListItem(
    long Id,
    string Level,
    string Status,
    string Message,
    string? Source,
    int? MachineId,
    string? MachineName,
    int? BranchId,
    string? BranchName,
    string? HandledBy,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    /// <summary>La detectó el motor de alertas (se resuelve sola al corregirse).</summary>
    bool Automatic,
    /// <summary>Alertas de red: el switch o router (y el puerto) donde está el problema.</summary>
    int? DeviceId = null,
    string? DeviceName = null);

public sealed record AlertList(PagedResult<AlertListItem> Data, AlertCounts Counts);

public sealed record AlertHistoryItem(DateTime At, string Action, string? UserName, string? Note);

public sealed record AlertDetail(
    long Id,
    string Level,
    string Status,
    string Message,
    string? Detail,
    string? Advice,
    string? Source,
    string? Code,
    bool Automatic,
    int? MachineId,
    string? MachineName,
    string? MachineStatus,
    int? BranchId,
    string? BranchName,
    int? RuleId,
    string? RuleName,
    string? HandledBy,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    /// <summary>Veces que el mismo problema se abrió en el mismo equipo en los últimos 30 días (sin contar esta).</summary>
    int Recurrences,
    IReadOnlyList<AlertHistoryItem> History,
    int? DeviceId = null,
    string? DeviceName = null,
    int? PortId = null);

/// <summary>Atender o resolver una alerta, con una nota opcional para el historial.</summary>
public sealed record AlertActionInput(string? Nota);

public sealed class AlertActionInputValidator : AbstractValidator<AlertActionInput>
{
    public AlertActionInputValidator() => RuleFor(x => x.Nota).MaximumLength(500).WithMessage("La nota admite hasta 500 caracteres.");
}

// ------------------------------------------------------------------ reglas (W-22, W-23)

/// <summary>Una métrica con su regla vigente para la empresa y los ajustes por sucursal.</summary>
public sealed record RuleItem(
    /// <summary>null = la empresa usa el valor por defecto de la sección 14.</summary>
    int? Id,
    string Metric,
    string Name,
    string Description,
    string Unit,
    bool LessIsWorse,
    bool HasThresholds,
    bool HasDuration,
    /// <summary>false = se evaluará cuando exista su módulo (impresoras SNMP, red).</summary>
    bool Available,
    decimal? Warning,
    decimal? Critical,
    int DurationMinutes,
    bool IsActive,
    IReadOnlyList<RuleOverride> Overrides,
    /// <summary>Servicio detenido: Centinela intenta iniciarlo solo (decisión 73).</summary>
    bool AutoRepair = false,
    /// <summary>Esta métrica admite reparación automática (hoy, solo servicio detenido).</summary>
    bool CanAutoRepair = false);

public sealed record RuleOverride(
    int Id, int BranchId, string BranchName, decimal? Warning, decimal? Critical, int DurationMinutes, bool IsActive, bool AutoRepair = false);

/// <summary>Crea o cambia la regla de una métrica para toda la empresa (SucursalId null) o para una sucursal.</summary>
/// <param name="RepararAuto">Solo servicio detenido; null = no se cambia.</param>
public sealed record RuleInput(
    string Metrica, int? SucursalId, decimal? UmbralAdvertencia, decimal? UmbralCritico, int DuracionMin, bool Activa, bool? RepararAuto = null);

public sealed class RuleInputValidator : AbstractValidator<RuleInput>
{
    public RuleInputValidator()
    {
        RuleFor(x => x.Metrica).Must(m => RuleCatalog.Find(m) is not null).WithMessage("Métrica no válida.");
        RuleFor(x => x.DuracionMin).InclusiveBetween(0, 1440).WithMessage("La duración va de 0 a 1440 minutos.");
        RuleFor(x => x.UmbralAdvertencia).GreaterThanOrEqualTo(0).When(x => x.UmbralAdvertencia is not null)
            .WithMessage("El umbral no puede ser negativo.");
        RuleFor(x => x.UmbralCritico).GreaterThanOrEqualTo(0).When(x => x.UmbralCritico is not null)
            .WithMessage("El umbral no puede ser negativo.");

        When(x => RuleCatalog.Find(x.Metrica) is { HasThresholds: true }, () =>
        {
            RuleFor(x => x).Must(x => x.UmbralAdvertencia is not null || x.UmbralCritico is not null)
                .OverridePropertyName("UmbralCritico").WithMessage("Indique al menos un umbral.");
            RuleFor(x => x.UmbralAdvertencia).LessThanOrEqualTo(100).When(x => RuleCatalog.Find(x.Metrica)!.Unit == "%")
                .WithMessage("Un porcentaje no puede pasar de 100.");
            RuleFor(x => x.UmbralCritico).LessThanOrEqualTo(100).When(x => RuleCatalog.Find(x.Metrica)!.Unit == "%")
                .WithMessage("Un porcentaje no puede pasar de 100.");
            RuleFor(x => x).Must(BeOrdered).OverridePropertyName("UmbralCritico")
                .WithMessage(x => RuleCatalog.Find(x.Metrica)!.LessIsWorse
                    ? "El umbral crítico debe ser menor que el de advertencia."
                    : "El umbral crítico debe ser mayor que el de advertencia.");
        });
    }

    private static bool BeOrdered(RuleInput x) =>
        x.UmbralAdvertencia is not decimal warning || x.UmbralCritico is not decimal critical
        || (RuleCatalog.Find(x.Metrica)!.LessIsWorse ? critical < warning : critical > warning);
}

using FluentValidation;

namespace MonitoreoTI.Application.Panel;

// ------------------------------------------------------------------ calendario de mantenimiento (W-27)

/// <summary>Mantenimientos con fecha (programada o realizada) entre From y To, inclusive.</summary>
public sealed record MaintenanceFilter(DateOnly From, DateOnly To, int? BranchId = null, int? MachineId = null);

/// <param name="Status">programado, vencido (pasó la fecha sin hacerse) o realizado.</param>
public sealed record MaintenanceItem(
    int Id, int MachineId, string MachineName, string MachineType, int BranchId, string BranchName, string Type, string? Description,
    DateOnly? ScheduledDate, DateOnly? CompletedDate, int? TechnicianId, string? TechnicianName, string Status);

/// <param name="Overdue">Programados que ya pasaron sin hacerse, en cualquier fecha (para el aviso del calendario).</param>
public sealed record MaintenanceList(IReadOnlyList<MaintenanceItem> Items, int Overdue);

/// <summary>Alta o edición. Un correctivo ya hecho se registra con su fecha de realización, sin programarlo.</summary>
public sealed record MaintenanceInput(int MaquinaId, string Tipo, string? Descripcion, DateOnly? FechaProgramada, DateOnly? FechaRealizada, int? TecnicoId);

public sealed record MaintenanceDoneInput(DateOnly? Fecha, string? Nota);

public static class MaintenanceRules
{
    public static string Status(DateOnly? scheduled, DateOnly? completed, DateOnly today) =>
        completed is not null ? "realizado" : scheduled is DateOnly date && date < today ? "vencido" : "programado";
}

public sealed class MaintenanceInputValidator : AbstractValidator<MaintenanceInput>
{
    public MaintenanceInputValidator()
    {
        RuleFor(x => x.MaquinaId).GreaterThan(0).WithMessage("Elija el equipo.");
        RuleFor(x => x.Tipo).Must(t => t is "preventivo" or "correctivo").WithMessage("El tipo es preventivo o correctivo.");
        RuleFor(x => x.Descripcion).MaximumLength(500);
        RuleFor(x => x).Must(x => x.FechaProgramada is not null || x.FechaRealizada is not null)
            .OverridePropertyName("FechaProgramada").WithMessage("Indique la fecha programada o la fecha en que se hizo.");
    }
}

public sealed class MaintenanceDoneInputValidator : AbstractValidator<MaintenanceDoneInput>
{
    public MaintenanceDoneInputValidator() => RuleFor(x => x.Nota).MaximumLength(500);
}

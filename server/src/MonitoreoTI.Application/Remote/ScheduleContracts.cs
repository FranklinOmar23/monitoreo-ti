using FluentValidation;

namespace MonitoreoTI.Application.Remote;

// ------------------------------------------------------------------ tareas programadas (6.5)

/// <param name="Objetivo">equipos (MaquinaIds), sucursal (SucursalId) o todos.</param>
/// <param name="Frecuencia">una_vez (Fecha), diaria, semanal (DiasSemana: lunes = 1 ... domingo = 64) o mensual (DiaMes).</param>
/// <param name="Hora">"HH:mm", hora de República Dominicana.</param>
public sealed record ScheduledTaskInput(
    string Nombre, int ScriptId, string Objetivo, int? SucursalId, IReadOnlyList<int>? MaquinaIds, string Frecuencia, string Hora,
    DateOnly? Fecha, int? DiasSemana, int? DiaMes, int? TimeoutS, bool Activa = true);

public sealed record ScheduledTaskItem(
    int Id, string Name, int ScriptId, string ScriptName, string Target, int? BranchId, string? BranchName, IReadOnlyList<int> MachineIds,
    string Frequency, string Time, DateOnly? Date, int? WeekDays, int? MonthDay, int TimeoutSeconds, bool Active,
    DateTime? NextRunAt, DateTime? LastRunAt, Guid? LastBatchId, string CreatedBy, string Description);

/// <summary>Una tarea según su frecuencia, con la hora en la de República Dominicana.</summary>
public sealed record ScheduleSpec(string Frequency, TimeOnly Time, DateOnly? Date, int? WeekDays, int? MonthDay);

public static class ScheduleRules
{
    /// <summary>República Dominicana: UTC-4 todo el año (sin horario de verano).</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-4);

    public const int Monday = 1, Tuesday = 2, Wednesday = 4, Thursday = 8, Friday = 16, Saturday = 32, Sunday = 64;

    public static bool IsFrequency(string? value) => value is "una_vez" or "diaria" or "semanal" or "mensual";

    public static bool IsTarget(string? value) => value is "equipos" or "sucursal" or "todos";

    public static bool TryParseTime(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);

    /// <summary>Bit del día (lunes = 1 ... domingo = 64).</summary>
    public static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    /// <summary>Siguiente ejecución en UTC estrictamente después de <paramref name="afterUtc"/>; null si ya no habrá más.</summary>
    public static DateTime? Next(ScheduleSpec spec, DateTime afterUtc)
    {
        var local = afterUtc + Offset;
        var today = DateOnly.FromDateTime(local);
        DateTime? candidate = null;

        switch (spec.Frequency)
        {
            case "una_vez":
                if (spec.Date is DateOnly date && date.ToDateTime(spec.Time) > local)
                    candidate = date.ToDateTime(spec.Time);
                break;
            case "diaria":
                candidate = today.ToDateTime(spec.Time);
                if (candidate <= local)
                    candidate = candidate.Value.AddDays(1);
                break;
            case "semanal":
                if (spec.WeekDays is not int days || (days & 127) == 0)
                    return null;
                for (var i = 0; i <= 7 && candidate is null; i++)
                {
                    var day = today.AddDays(i);
                    var at = day.ToDateTime(spec.Time);
                    if ((days & DayBit(day.DayOfWeek)) != 0 && at > local)
                        candidate = at;
                }
                break;
            case "mensual":
                if (spec.MonthDay is not int monthDay || monthDay is < 1 or > 31)
                    return null;
                for (var i = 0; i <= 2 && candidate is null; i++)
                {
                    var month = new DateOnly(today.Year, today.Month, 1).AddMonths(i);
                    // Un mes más corto usa su último día (el 31 en abril es el 30).
                    var day = Math.Min(monthDay, DateTime.DaysInMonth(month.Year, month.Month));
                    var at = new DateOnly(month.Year, month.Month, day).ToDateTime(spec.Time);
                    if (at > local)
                        candidate = at;
                }
                break;
        }

        return candidate is DateTime value ? DateTime.SpecifyKind(value - Offset, DateTimeKind.Utc) : null;
    }

    /// <summary>Texto para el técnico: "Todos los días a las 02:00", "Lunes y viernes a las 18:30"...</summary>
    public static string Describe(ScheduleSpec spec)
    {
        var at = $"a las {spec.Time:HH\\:mm}";
        return spec.Frequency switch
        {
            "una_vez" => $"Una vez, el {spec.Date:dd/MM/yyyy} {at}",
            "diaria" => $"Todos los días {at}",
            "semanal" => $"{WeekDaysText(spec.WeekDays ?? 0)} {at}",
            "mensual" => $"El día {spec.MonthDay} de cada mes {at}",
            _ => "",
        };
    }

    private static readonly string[] DayNames = ["lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo"];

    private static string WeekDaysText(int days)
    {
        if ((days & 127) == 127)
            return "Todos los días";
        if ((days & 127) == 31)
            return "De lunes a viernes";
        var names = DayNames.Where((_, i) => (days & (1 << i)) != 0).ToList();
        var text = names.Count <= 1 ? string.Join("", names) : $"{string.Join(", ", names[..^1])} y {names[^1]}";
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}

public sealed class ScheduledTaskInputValidator : AbstractValidator<ScheduledTaskInput>
{
    public ScheduledTaskInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre.").MaximumLength(150);
        RuleFor(x => x.ScriptId).GreaterThan(0).WithMessage("Elija el script.");
        RuleFor(x => x.Objetivo).Must(ScheduleRules.IsTarget).WithMessage("El objetivo es equipos, sucursal o todos.");
        RuleFor(x => x.SucursalId).NotNull().When(x => x.Objetivo == "sucursal").WithMessage("Elija la sucursal.");
        RuleFor(x => x.MaquinaIds).NotEmpty().When(x => x.Objetivo == "equipos").WithMessage("Elija al menos un equipo.")
            .Must(ids => ids is null || ids.Count <= RemoteRules.MaxMachinesPerRun).WithMessage($"Como máximo {RemoteRules.MaxMachinesPerRun} equipos.");
        RuleFor(x => x.Frecuencia).Must(ScheduleRules.IsFrequency).WithMessage("La frecuencia es una vez, diaria, semanal o mensual.");
        RuleFor(x => x.Hora).Must(h => ScheduleRules.TryParseTime(h, out _)).WithMessage("La hora va como HH:mm (por ejemplo 02:00).");
        RuleFor(x => x.Fecha).NotNull().When(x => x.Frecuencia == "una_vez").WithMessage("Elija la fecha.");
        RuleFor(x => x.DiasSemana).NotNull().InclusiveBetween(1, 127).When(x => x.Frecuencia == "semanal").WithMessage("Elija al menos un día.");
        RuleFor(x => x.DiaMes).NotNull().InclusiveBetween(1, 31).When(x => x.Frecuencia == "mensual").WithMessage("El día del mes va de 1 a 31.");
        RuleFor(x => x.TimeoutS).InclusiveBetween(10, RemoteRules.MaxTimeoutSeconds)
            .WithMessage($"El tiempo límite va de 10 a {RemoteRules.MaxTimeoutSeconds} segundos.");
    }
}

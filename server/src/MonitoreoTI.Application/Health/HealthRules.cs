using System.Globalization;

namespace MonitoreoTI.Application.Health;

public enum FindingLevel
{
    Warning,
    Critical,
}

/// <summary>Un problema encontrado en un equipo. Code identifica el problema: hay una sola alerta abierta por código.</summary>
public sealed record Finding(string Code, FindingLevel Level, string Message, string Detail, string Advice, string? Source, string? Metric);

/// <summary>Umbrales de una regla (tabla reglas_alerta). null = ese nivel no aplica.</summary>
public sealed record RuleThreshold(decimal? Warning, decimal? Critical, int DurationMinutes = 0, bool Active = true);

/// <summary>Reglas de la empresa; si no tiene una, se usan las de la sección 14.</summary>
public sealed class RuleSet
{
    public static readonly IReadOnlyDictionary<string, RuleThreshold> Defaults = new Dictionary<string, RuleThreshold>
    {
        ["disco"] = new(80, 95),
        ["cpu"] = new(85, 95, 15),
        ["ram"] = new(85, 95),
        ["temperatura"] = new(80, 90),
        ["sin_reportar"] = new(10, 30),
        ["errores_criticos"] = new(1, 3),
        ["servicio_detenido"] = new(null, 1),
        ["antivirus"] = new(null, 0),
        ["toner"] = new(20, 5),
        ["puerto_errores"] = new(100, 1000),
        ["puerto_caido"] = new(null, 0),
        ["switch_sin_respuesta"] = new(10, 30),
    };

    private readonly IReadOnlyDictionary<string, RuleThreshold> _rules;

    public RuleSet(IReadOnlyDictionary<string, RuleThreshold>? companyRules = null) => _rules = companyRules ?? new Dictionary<string, RuleThreshold>();

    public RuleThreshold? For(string metric)
    {
        var rule = _rules.GetValueOrDefault(metric) ?? Defaults.GetValueOrDefault(metric);
        return rule is { Active: true } ? rule : null;
    }
}

public sealed record VolumeSnapshot(string Drive, decimal TotalGb, decimal FreeGb);
public sealed record MetricSample(DateTime At, decimal? Cpu, decimal? Ram, decimal? Temperature);
public sealed record EventSnapshot(DateTime At, string? Source, int? Code);
public sealed record StoppedService(string Name, string? DisplayName);
public sealed record ComponentSnapshot(string Type, string? Description, string Health, string? Detail, string? Capacity = null);

/// <summary>Lo que se sabe de un equipo al evaluarlo.</summary>
public sealed record MachineSnapshot(
    int Id,
    string Name,
    bool Disconnected,
    DateTime? LastReportAt,
    IReadOnlyList<VolumeSnapshot> Volumes,
    IReadOnlyList<MetricSample> RecentMetrics,
    IReadOnlyList<EventSnapshot> CriticalEvents24h,
    IReadOnlyList<StoppedService> StoppedServices,
    IReadOnlyList<ComponentSnapshot> Components);

/// <summary>
/// Reglas de alerta por defecto (sección 14), puras: dado un equipo y las reglas de su empresa, devuelve sus problemas
/// con una explicación en lenguaje simple y qué hacer. Lo que cambia la base de datos está en el motor de alertas.
/// </summary>
public static class HealthRules
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-DO");

    public static IReadOnlyList<Finding> Evaluate(MachineSnapshot machine, RuleSet rules, DateTime nowUtc)
    {
        var findings = new List<Finding>();

        NotReporting(machine, rules, nowUtc, findings);

        // Con el equipo apagado los últimos datos ya no describen su estado actual.
        var fresh = !machine.Disconnected && machine.LastReportAt is { } last && nowUtc - last < TimeSpan.FromMinutes(10);
        if (fresh)
        {
            Disks(machine, rules, findings);
            Cpu(machine, rules, nowUtc, findings);
            Ram(machine, rules, nowUtc, findings);
            Temperature(machine, rules, findings);
            Services(machine, rules, findings);
        }

        CriticalErrors(machine, rules, findings);
        DiskHealth(machine, findings);
        Antivirus(machine, rules, findings);
        Toner(machine, rules, findings);
        return findings;
    }

    /// <summary>Nivel según los umbrales de la regla, o null si no se alcanza ninguno.</summary>
    private static FindingLevel? Level(decimal value, RuleThreshold rule, bool lessIsWorse = false)
    {
        bool Exceeds(decimal? limit) => limit is decimal l && (lessIsWorse ? value < l : value > l);
        return Exceeds(rule.Critical) ? FindingLevel.Critical : Exceeds(rule.Warning) ? FindingLevel.Warning : null;
    }

    private static string Number(decimal value, string format = "0") => value.ToString(format, Es);

    private static void Disks(MachineSnapshot machine, RuleSet rules, List<Finding> findings)
    {
        if (rules.For("disco") is not { } rule)
            return;

        foreach (var volume in machine.Volumes.Where(v => v.TotalGb > 0))
        {
            var used = 100m * (volume.TotalGb - volume.FreeGb) / volume.TotalGb;
            if (Level(used, rule) is not { } level)
                continue;

            findings.Add(new Finding(
                $"disco_lleno:{volume.Drive}",
                level,
                $"Disco {volume.Drive} al {Number(used)} % de su capacidad",
                $"Quedan {Number(volume.FreeGb, "0.#")} GB libres de {Number(volume.TotalGb)} GB. Con el disco lleno, " +
                "Windows y los programas fallan o no pueden guardar.",
                "Libera espacio (archivos temporales, descargas, papelera) o amplía el disco.",
                $"Unidad {volume.Drive} · {Number(volume.TotalGb - volume.FreeGb, "0.#")} / {Number(volume.TotalGb)} GB",
                "disco"));
        }
    }

    /// <summary>Promedio de las muestras de la ventana, si la ventana tiene suficientes datos (un 80 % de los minutos).</summary>
    private static decimal? Sustained(MachineSnapshot machine, DateTime nowUtc, int minutes, Func<MetricSample, decimal?> pick)
    {
        var from = nowUtc.AddMinutes(-minutes);
        var values = machine.RecentMetrics.Where(m => m.At >= from).Select(pick).OfType<decimal>().ToList();
        var needed = Math.Max(1, (int)Math.Ceiling(minutes * 0.8));
        return values.Count >= needed ? values.Average() : null;
    }

    private static void Cpu(MachineSnapshot machine, RuleSet rules, DateTime nowUtc, List<Finding> findings)
    {
        if (rules.For("cpu") is not { } rule)
            return;

        // La advertencia exige la duración de la regla; el crítico, el doble (15 y 30 min por defecto).
        var minutes = Math.Max(rule.DurationMinutes, 1);
        var critical = Sustained(machine, nowUtc, minutes * 2, m => m.Cpu);
        var warning = Sustained(machine, nowUtc, minutes, m => m.Cpu);

        (FindingLevel Level, decimal Value, int Minutes)? hit =
            critical is { } c && rule.Critical is { } cl && c > cl ? (FindingLevel.Critical, c, minutes * 2)
            : warning is { } w && rule.Warning is { } wl && w > wl ? (FindingLevel.Warning, w, minutes)
            : null;
        if (hit is not { } found)
            return;

        findings.Add(new Finding(
            "cpu_alta",
            found.Level,
            $"Procesador al {Number(found.Value)} % durante {found.Minutes} min",
            "El equipo trabaja al límite de su procesador: se siente lento y los programas tardan en responder.",
            "Revisa en Procesos qué programa consume más y ciérralo o actualízalo.",
            $"Promedio de los últimos {found.Minutes} min",
            "cpu"));
    }

    private static void Ram(MachineSnapshot machine, RuleSet rules, DateTime nowUtc, List<Finding> findings)
    {
        if (rules.For("ram") is not { } rule)
            return;

        // Un pico de RAM es normal: se mira el promedio de al menos 5 minutos.
        var minutes = Math.Max(rule.DurationMinutes, 5);
        if (Sustained(machine, nowUtc, minutes, m => m.Ram) is not { } value || Level(value, rule) is not { } level)
            return;

        findings.Add(new Finding(
            "ram_alta",
            level,
            $"Memoria RAM al {Number(value)} % sostenido",
            "Con la memoria casi llena el equipo se vuelve lento y los programas pueden cerrarse solos.",
            "Cierra los programas que no se usan o amplía la memoria RAM.",
            $"Promedio de los últimos {minutes} min",
            "ram"));
    }

    private static void Temperature(MachineSnapshot machine, RuleSet rules, List<Finding> findings)
    {
        if (rules.For("temperatura") is not { } rule)
            return;
        var latest = machine.RecentMetrics.OrderByDescending(m => m.At).FirstOrDefault(m => m.Temperature is not null);
        if (latest?.Temperature is not { } celsius || Level(celsius, rule) is not { } level)
            return;

        findings.Add(new Finding(
            "temperatura_alta",
            level,
            $"Temperatura del procesador en {Number(celsius)} °C",
            "El calor excesivo hace que el equipo se ponga lento y acorta la vida de sus componentes.",
            "Limpia el polvo de ventiladores y rejillas y revisa que el ventilador gire.",
            "Sensor térmico del equipo",
            "temperatura"));
    }

    private static void NotReporting(MachineSnapshot machine, RuleSet rules, DateTime nowUtc, List<Finding> findings)
    {
        if (rules.For("sin_reportar") is not { } rule || machine.LastReportAt is not { } last)
            return;

        var minutes = (decimal)(nowUtc - last).TotalMinutes;
        if (Level(minutes, rule) is not { } level)
            return;

        var elapsed = minutes >= 120 ? $"{Number(minutes / 60)} horas" : $"{Number(minutes)} min";
        findings.Add(new Finding(
            "sin_reportar",
            level,
            $"{machine.Name} lleva {elapsed} sin reportar",
            "El agente no se comunica: el equipo puede estar apagado, sin red o con el servicio del agente detenido.",
            "Verifica que el equipo esté encendido y conectado a la red.",
            "Último reporte del agente",
            "sin_reportar"));
    }

    private static void CriticalErrors(MachineSnapshot machine, RuleSet rules, List<Finding> findings)
    {
        if (rules.For("errores_criticos") is not { } rule || machine.CriticalEvents24h.Count == 0)
            return;
        if (Level(machine.CriticalEvents24h.Count, rule with { Warning = rule.Warning - 1, Critical = rule.Critical - 1 }) is not { } level)
            return;

        // El problema que más se repite da el título y el consejo.
        var top = machine.CriticalEvents24h
            .GroupBy(e => (e.Source, e.Code))
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(e => e.At))
            .First();
        var explanation = EventCatalog.Explain(top.Key.Source, top.Key.Code ?? 0);
        var count = machine.CriticalEvents24h.Count;
        var times = count == 1 ? "1 vez" : $"{count} veces";

        findings.Add(new Finding(
            "errores_criticos",
            level,
            explanation?.Title ?? "Errores críticos del sistema",
            $"{explanation?.Detail ?? "Windows registró errores críticos."} Ocurrió {times} en las últimas 24 horas.",
            explanation?.Advice ?? "Revisa la pestaña Errores para ver el detalle de cada uno.",
            $"{ShortSource(top.Key.Source)} {top.Key.Code} · último: {top.Max(e => e.At):dd/MM HH:mm} UTC",
            "errores_criticos"));
    }

    private static string ShortSource(string? source) =>
        source?.Replace("Microsoft-Windows-", "", StringComparison.OrdinalIgnoreCase) ?? "Windows";

    private static void Services(MachineSnapshot machine, RuleSet rules, List<Finding> findings)
    {
        if (rules.For("servicio_detenido") is null)
            return;

        foreach (var service in machine.StoppedServices)
        {
            var name = service.DisplayName ?? service.Name;
            findings.Add(new Finding(
                $"servicio:{service.Name}",
                FindingLevel.Critical,
                $"Servicio detenido: {name}",
                "Es un servicio que arranca con Windows: estaba en ejecución y se detuvo con un error. Mientras esté detenido, lo que hace deja de funcionar.",
                "Inícialo de nuevo desde Servicios (services.msc) y revisa en Errores por qué se detuvo.",
                $"Servicio {service.Name}",
                "servicio_detenido"));
        }
    }

    private static void DiskHealth(MachineSnapshot machine, List<Finding> findings)
    {
        foreach (var disk in machine.Components.Where(c => c.Type == "disco" && c.Health is "advertencia" or "critico"))
        {
            var critical = disk.Health == "critico";
            var damaged = disk.Detail?.Contains("sectores", StringComparison.OrdinalIgnoreCase) == true;
            var name = disk.Description ?? "principal";
            findings.Add(new Finding(
                $"disco_salud:{name}",
                critical ? FindingLevel.Critical : FindingLevel.Warning,
                damaged ? "Disco con sectores dañados" : critical ? "El disco está fallando" : "El disco muestra señales de desgaste",
                $"El disco {name} {(disk.Detail is null ? "reporta problemas de salud" : $"reporta: {disk.Detail.ToLowerInvariant()}")}. " +
                "Puede perder archivos o dejar de arrancar.",
                critical ? "Respalda los datos hoy y programa el cambio del disco."
                         : "Respalda los datos y vigila el disco: si los sectores dañados aumentan, reemplázalo.",
                $"SMART · {disk.Detail ?? "estado de salud del disco"}",
                null));
        }
    }

    /// <summary>Tóner (sección 14): menos de 20 % advertencia, menos de 5 % crítico. Los depósitos de residuos no cuentan.</summary>
    private static void Toner(MachineSnapshot machine, RuleSet rules, List<Finding> findings)
    {
        if (rules.For("toner") is not { } rule)
            return;
        foreach (var supply in machine.Components.Where(c => c.Type == "toner"))
        {
            if (supply.Capacity is not { } text || !decimal.TryParse(text.TrimEnd('%', ' '), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
                continue;
            if (Level(percent, rule, lessIsWorse: true) is not { } level)
                continue;
            var name = supply.Description ?? "consumible";
            findings.Add(new Finding(
                $"toner:{name}",
                level,
                $"{name} al {percent:0} %",
                level == FindingLevel.Critical ? "El consumible está por agotarse: la impresora puede dejar de imprimir." : "El consumible se está acabando.",
                level == FindingLevel.Critical ? "Cámbialo hoy." : "Pide el repuesto para tenerlo a mano.",
                $"SNMP · prtMarkerSuppliesLevel · {percent:0} %",
                "toner"));
        }
    }

    private static void Antivirus(MachineSnapshot machine, RuleSet rules, List<Finding> findings)
    {
        if (rules.For("antivirus") is null)
            return;

        // Con dos antivirus uno suele quedar pasivo: solo es un problema si ninguno está activo.
        var antivirus = machine.Components.Where(c => c.Type == "antivirus").ToList();
        if (antivirus.Count == 0 || antivirus.Any(a => a.Health != "critico"))
            return;

        findings.Add(new Finding(
            "antivirus",
            FindingLevel.Critical,
            "Antivirus desactivado",
            "El equipo no tiene ninguna protección activa contra virus.",
            "Activa el antivirus o revisa su licencia.",
            string.Join(", ", antivirus.Select(a => a.Description)),
            "antivirus"));
    }
}

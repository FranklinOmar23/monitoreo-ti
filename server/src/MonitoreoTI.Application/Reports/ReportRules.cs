namespace MonitoreoTI.Application.Reports;

/// <summary>Reglas puras de los reportes (decisión 92): periodo anterior, tramos del mapa de calor y "requiere atención".</summary>
public static class ReportRules
{
    /// <summary>Hasta cuántos días el mapa de calor va por día; por encima, por semana.</summary>
    public const int DailyBucketsUpTo = 92;

    /// <summary>Por debajo de este porcentaje en línea un equipo cuenta con baja disponibilidad.</summary>
    public const decimal LowAvailability = 90;

    /// <summary>Por debajo de este porcentaje la baja disponibilidad ya es crítica.</summary>
    public const decimal CriticalAvailability = 80;

    /// <summary>Libre por debajo de esto (porcentaje) un disco lleno ya es crítico.</summary>
    public const decimal CriticalDiskPercent = 5;

    /// <summary>Cuántos equipos de ejemplo lleva cada punto de "requiere atención".</summary>
    public const int SampleMachines = 5;

    public static int Days(ReportFilter filter) => filter.To.DayNumber - filter.From.DayNumber + 1;

    /// <summary>El periodo de igual duración que termina el día antes de que empiece este.</summary>
    public static ReportFilter Previous(ReportFilter filter)
    {
        var days = Days(filter);
        return filter with { From = filter.From.AddDays(-days), To = filter.From.AddDays(-1) };
    }

    /// <summary>Todos los días del periodo, en orden.</summary>
    public static IReadOnlyList<DateOnly> EachDay(ReportFilter filter) =>
        Enumerable.Range(0, Days(filter)).Select(filter.From.AddDays).ToList();

    /// <summary>Tamaño del tramo del mapa de calor: 1 día, o 7 si el periodo pasa de <see cref="DailyBucketsUpTo"/> días.</summary>
    public static int BucketDays(ReportFilter filter) => Days(filter) > DailyBucketsUpTo ? 7 : 1;

    /// <summary>El inicio de cada tramo; el último puede quedar más corto.</summary>
    public static IReadOnlyList<DateOnly> Buckets(ReportFilter filter)
    {
        var size = BucketDays(filter);
        return Enumerable.Range(0, (Days(filter) + size - 1) / size).Select(i => filter.From.AddDays(i * size)).ToList();
    }

    /// <summary>
    /// Porcentaje en línea de un tramo: horas con métricas frente a las horas en que el equipo existía y el tramo ya había
    /// pasado. Null si no hubo ninguna hora medible (el equipo aún no existía o el tramo es futuro).
    /// </summary>
    public static decimal? Cell(DateTime bucketStart, DateTime bucketEnd, DateTime createdAt, DateTime now, int hoursOnline)
    {
        var from = createdAt > bucketStart ? createdAt : bucketStart;
        var until = now < bucketEnd ? now : bucketEnd;
        if (until <= from)
            return null;
        var possible = Math.Max(1, (int)Math.Ceiling((until - from).TotalHours));
        return Math.Round(100m * Math.Min(possible, hoursOnline) / possible, 1);
    }

    /// <summary>Promedio de cada columna sin contar los huecos; null si toda la columna está vacía.</summary>
    public static IReadOnlyList<decimal?> ColumnAverages(IReadOnlyList<IReadOnlyList<decimal?>> rows, int columns) =>
        Enumerable.Range(0, columns)
            .Select(i =>
            {
                var values = rows.Select(r => r[i]).OfType<decimal>().ToList();
                return values.Count == 0 ? (decimal?)null : Math.Round(values.Average(), 1);
            })
            .ToList();

    /// <summary>Una serie por día del periodo; los días sin dato valen <paramref name="missing"/>.</summary>
    public static IReadOnlyList<decimal?> Daily<T>(
        ReportFilter filter, IEnumerable<T> items, Func<T, DateOnly> date, Func<T, decimal?> value, decimal? missing = 0)
    {
        var byDay = items.GroupBy(date).ToDictionary(g => g.Key, g => value(g.First()));
        return EachDay(filter).Select(d => byDay.TryGetValue(d, out var v) ? v : missing).ToList();
    }

    /// <summary>
    /// Lo que requiere atención, de lo más grave a lo menos: primero los críticos y, dentro de cada nivel, los que
    /// afectan a más equipos.
    /// </summary>
    public static IReadOnlyList<AttentionItem> Attention(AttentionInput input)
    {
        var items = new List<AttentionItem>();

        void Add(string code, string level, string report, IEnumerable<MachineRef> machines, int? count = null)
        {
            var distinct = machines.DistinctBy(m => m.Id).ToList();
            var total = count ?? distinct.Count;
            if (total > 0)
                items.Add(new AttentionItem(code, level, report, total, distinct.Take(SampleMachines).ToList()));
        }

        var disks = input.DiskSecurity;
        Add("disco_falla", disks.DiskHealth.Any(d => d.Health == "critico") ? "critico" : "advertencia", "discos-seguridad",
            disks.DiskHealth.OrderBy(d => d.Health == "critico" ? 0 : 1).Select(d => new MachineRef(d.MachineId, d.MachineName)));
        Add("sin_proteccion", "critico", "discos-seguridad",
            disks.Security.Where(s => s.Issue != "antivirus_desactualizado").Select(s => new MachineRef(s.MachineId, s.MachineName)));
        Add("disco_lleno", disks.LowDisks.Any(d => d.FreePercent < CriticalDiskPercent) ? "critico" : "advertencia", "discos-seguridad",
            disks.LowDisks.OrderBy(d => d.FreePercent).Select(d => new MachineRef(d.MachineId, d.MachineName)));
        Add("antivirus_desactualizado", "advertencia", "discos-seguridad",
            disks.Security.Where(s => s.Issue == "antivirus_desactualizado").Select(s => new MachineRef(s.MachineId, s.MachineName)));

        var low = input.Availability.Machines.Where(m => m.Percent < LowAvailability).OrderBy(m => m.Percent).ToList();
        Add("baja_disponibilidad", low.Any(m => m.Percent < CriticalAvailability) ? "critico" : "advertencia", "disponibilidad",
            low.Select(m => new MachineRef(m.Id, m.Name)));

        if (input.Alerts.Open > 0)
            Add("alertas_abiertas", "advertencia", "alertas",
                input.Alerts.TopMachines.Select(m => new MachineRef(m.Id, m.Name)), input.Alerts.Open);

        if (input.Performance is { } performance)
        {
            Add("ampliar_ram", "advertencia", "rendimiento",
                performance.Machines.Where(m => m.Advice.Contains("ampliar_ram")).Select(m => new MachineRef(m.Id, m.Name)));
            Add("revisar_cpu", "advertencia", "rendimiento",
                performance.Machines.Where(m => m.Advice.Contains("revisar_cpu")).Select(m => new MachineRef(m.Id, m.Name)));
        }
        if (input.Tickets is { Maintenance.Overdue: > 0 } tickets)
            Add("mantenimientos_vencidos", "advertencia", "tickets", [], tickets.Maintenance.Overdue);

        return items
            .OrderBy(i => i.Level == "critico" ? 0 : 1)
            .ThenByDescending(i => i.Count)
            .ToList();
    }

    /// <summary>Por sucursal: equipos, disponibilidad promedio y alertas del periodo; las que más alertas tienen primero.</summary>
    public static IReadOnlyList<BranchSummary> Branches(AvailabilityReport availability, AlertReport alerts)
    {
        var alertsByBranch = alerts.ByBranch.ToDictionary(b => b.Key, b => b.Count);
        var names = availability.Machines.Select(m => m.BranchName).Union(alertsByBranch.Keys);
        return names
            .Select(name =>
            {
                var machines = availability.Machines.Where(m => m.BranchName == name).ToList();
                return new BranchSummary(
                    name, machines.Count,
                    machines.Count == 0 ? null : Math.Round(machines.Average(m => m.Percent), 1),
                    alertsByBranch.GetValueOrDefault(name));
            })
            .OrderByDescending(b => b.Alerts).ThenBy(b => b.Name)
            .ToList();
    }
}

/// <summary>Los reportes de los que sale "requiere atención"; los avanzados son null si el plan no los incluye.</summary>
public sealed record AttentionInput(
    DiskSecurityReport DiskSecurity, AvailabilityReport Availability, AlertReport Alerts,
    PerformanceReport? Performance = null, TicketReport? Tickets = null);

using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Reports;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Reports;

/// <summary>
/// Reportes (W-35). La empresa la filtran Row-Level Security y el filtro global; aquí se agregan las sucursales del
/// usuario y los filtros del reporte. Los avanzados exigen el módulo reportes_avanzados en el plan vigente.
/// </summary>
public sealed class ReportQueries(MonitoreoDbContext db, ICurrentUser user, TimeProvider clock)
{
    /// <summary>Hasta cuántos días puede abarcar un reporte.</summary>
    public const int MaxDays = 400;

    /// <summary>Libre por debajo de esto (porcentaje) cuenta como disco lleno.</summary>
    public const decimal LowDiskPercent = 15;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static string Text<TEnum>(TEnum value) where TEnum : struct, Enum => PanelQueries.Text(value);

    public async Task<bool> AdvancedAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Now);
        return await db.Subscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.StartDate <= today && (s.EndDate == null || s.EndDate >= today))
            .AnyAsync(s => db.PlanModules.Any(m => m.PlanId == s.PlanId && m.Module == ModuleKey.AdvancedReports), ct);
    }

    /// <summary>Si el usuario o el filtro limitan las sucursales o el departamento (la foto diaria es de toda la empresa).</summary>
    private async Task<bool> NarrowedAsync(ReportFilter filter, CancellationToken ct) =>
        filter.BranchId is not null || filter.DepartmentId is not null || await user.AllowedBranchIdsAsync(ct) is not null;

    private async Task<IQueryable<Machine>> MachinesAsync(ReportFilter filter, CancellationToken ct, bool includeRetired = false)
    {
        var query = db.Machines.AsNoTracking();
        if (!includeRetired)
            query = query.Where(m => m.Status != MachineStatus.Retired);
        if (await user.AllowedBranchIdsAsync(ct) is { } branches)
            query = query.Where(m => branches.Contains(m.BranchId));
        if (filter.BranchId is int branchId)
            query = query.Where(m => m.BranchId == branchId);
        if (filter.DepartmentId is int departmentId)
            query = query.Where(m => m.DepartmentId == departmentId);
        return query;
    }

    private static (DateTime Start, DateTime End) Range(ReportFilter filter) =>
        (filter.From.ToDateTime(TimeOnly.MinValue), filter.To.AddDays(1).ToDateTime(TimeOnly.MinValue));

    private static double? Hours(IEnumerable<double> minutes)
    {
        var list = minutes.ToList();
        return list.Count == 0 ? null : Math.Round(list.Average() / 60, 1);
    }

    // ------------------------------------------------------------------ inventario

    public async Task<InventoryReport> InventoryAsync(ReportFilter filter, CancellationToken ct)
    {
        var machines = await MachinesAsync(filter, ct);
        var rows = await machines
            .OrderBy(m => m.Branch!.Name).ThenBy(m => m.Name)
            .Select(m => new
            {
                m.Id, m.Name, m.Type, Branch = m.Branch!.Name, Department = m.Department != null ? m.Department.Name : null, m.AssignedTo,
                m.OperatingSystem, m.RamTotalMb, m.Brand, m.Model, m.SerialNumber, m.IpAddress, m.MacAddress, m.Status, m.WarrantyUntil,
                m.CreatedAt, m.LastReportAt,
                Processor = m.Components.Where(c => c.Type == "cpu").Select(c => c.Description).FirstOrDefault(),
                Disks = m.Components.Where(c => c.Type == "disco").Select(c => c.Capacity ?? c.Description).ToList(),
            })
            .ToListAsync(ct);

        var result = rows.Select(m => new InventoryRow(
            m.Id, m.Name, Text(m.Type), m.Branch, m.Department, m.AssignedTo, m.OperatingSystem, m.Processor,
            m.RamTotalMb is int mb ? (int)Math.Round(mb / 1024.0) : null,
            m.Disks.Count == 0 ? null : string.Join(" + ", m.Disks.Where(d => d is not null)),
            m.Brand, m.Model, m.SerialNumber, m.IpAddress, m.MacAddress, Text(m.Status), m.WarrantyUntil, m.CreatedAt, m.LastReportAt)).ToList();

        return new InventoryReport(
            result.Count,
            result.GroupBy(r => r.Type).Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList(),
            result.GroupBy(r => r.OperatingSystem ?? "Sin dato").Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList(),
            result);
    }

    // ------------------------------------------------------------------ disponibilidad

    public async Task<AvailabilityReport> AvailabilityAsync(ReportFilter filter, CancellationToken ct)
    {
        var (start, end) = Range(filter);
        var until = end < Now ? end : Now;
        var machines = await MachinesAsync(filter, ct);

        var list = await machines
            .Where(m => m.CreatedAt < end)
            .Select(m => new { m.Id, m.Name, Branch = m.Branch!.Name, m.CreatedAt, m.LastReportAt })
            .ToListAsync(ct);
        var hours = await db.HourlyMetrics.AsNoTracking()
            .Where(h => h.Hour >= start && h.Hour < end && machines.Select(m => m.Id).Contains(h.MachineId))
            .GroupBy(h => h.MachineId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        var rows = list.Select(m =>
            {
                // El reloj de SQL Server puede ir unos segundos por delante: un equipo recién dado de alta cuenta una hora.
                var from = m.CreatedAt > start ? m.CreatedAt : start;
                var inPeriod = Math.Max(1, (int)Math.Ceiling((until - from).TotalHours));
                var online = Math.Min(inPeriod, hours.GetValueOrDefault(m.Id));
                return new AvailabilityRow(m.Id, m.Name, m.Branch, online, inPeriod, Math.Round(100m * online / inPeriod, 1), m.LastReportAt);
            })
            .OrderBy(r => r.Percent).ThenBy(r => r.Name)
            .ToList();

        var days = await NarrowedAsync(filter, ct)
            ? []
            : await db.DailyFleetStatus.AsNoTracking()
                .Where(d => d.Date >= filter.From && d.Date <= filter.To)
                .OrderBy(d => d.Date)
                .Select(d => new DailyHealth(d.Date, d.Healthy, d.Warning, d.Critical, d.Disconnected))
                .ToListAsync(ct);

        return new AvailabilityReport(rows.Count == 0 ? 0 : Math.Round(rows.Average(r => r.Percent), 1), days, rows);
    }

    // ------------------------------------------------------------------ alertas

    public async Task<AlertReport> AlertsAsync(ReportFilter filter, CancellationToken ct)
    {
        var (start, end) = Range(filter);
        var machines = await MachinesAsync(filter, ct, includeRetired: true);
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.CreatedAt >= start && a.CreatedAt < end && a.MachineId != null)
            .Join(machines, a => a.MachineId, m => m.Id, (a, m) => new
            {
                a.Level, a.Status, a.Code, a.CreatedAt, a.ResolvedAt, MachineId = m.Id, MachineName = m.Name, Branch = m.Branch!.Name,
            })
            .ToListAsync(ct);

        var perDay = alerts
            .GroupBy(a => DateOnly.FromDateTime(a.CreatedAt))
            .OrderBy(g => g.Key)
            .Select(g => new DailyAlerts(g.Key, g.Count(a => a.Level == AlertLevel.Critical), g.Count(a => a.Level == AlertLevel.Warning)))
            .ToList();

        return new AlertReport(
            alerts.Count,
            alerts.Count(a => a.Level == AlertLevel.Critical),
            alerts.Count(a => a.Level == AlertLevel.Warning),
            alerts.Count(a => a.Status != AlertStatus.Resolved),
            Hours(alerts.Where(a => a.ResolvedAt is not null).Select(a => (a.ResolvedAt!.Value - a.CreatedAt).TotalMinutes)),
            perDay,
            alerts.GroupBy(a => a.Code ?? "otro").Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).Take(12).ToList(),
            alerts.GroupBy(a => a.Branch).Select(g => new CountItem(g.Key, g.Count())).OrderByDescending(c => c.Count).ToList(),
            alerts.GroupBy(a => new { a.MachineId, a.MachineName, a.Branch })
                .Select(g => new AlertMachine(g.Key.MachineId, g.Key.MachineName, g.Key.Branch, g.Count(), g.Count(a => a.Level == AlertLevel.Critical)))
                .OrderByDescending(m => m.Count).ThenByDescending(m => m.Critical).Take(10).ToList());
    }

    // ------------------------------------------------------------------ discos y seguridad

    public async Task<DiskSecurityReport> DiskSecurityAsync(ReportFilter filter, CancellationToken ct)
    {
        var machines = await MachinesAsync(filter, ct);

        var volumes = await db.Volumes.AsNoTracking()
            .Where(v => v.TotalGb > 0 && v.FreeGb * 100 < v.TotalGb * LowDiskPercent)
            .Join(machines, v => v.MachineId, m => m.Id, (v, m) => new { v.Drive, v.TotalGb, v.FreeGb, m.Id, m.Name, Branch = m.Branch!.Name })
            .ToListAsync(ct);
        var lowDisks = volumes
            .Select(v => new LowDiskRow(v.Id, v.Name, v.Branch, v.Drive, Math.Round(v.TotalGb, 1), Math.Round(v.FreeGb, 1), Math.Round(100 * v.FreeGb / v.TotalGb, 1)))
            .OrderBy(v => v.FreePercent)
            .ToList();

        var components = await db.Components.AsNoTracking()
            .Where(c => c.Type == "disco" || c.Type == "antivirus" || c.Type == "firewall")
            .Join(machines, c => c.MachineId, m => m.Id, (c, m) => new { c.Type, c.Description, c.HealthStatus, c.Detail, m.Id, m.Name, Branch = m.Branch!.Name })
            .ToListAsync(ct);

        var diskHealth = components
            .Where(c => c.Type == "disco" && c.HealthStatus is HealthStatus.Warning or HealthStatus.Critical)
            .OrderBy(c => c.HealthStatus == HealthStatus.Critical ? 0 : 1).ThenBy(c => c.Name)
            .Select(c => new DiskHealthRow(c.Id, c.Name, c.Branch, c.Description, Text(c.HealthStatus), c.Detail))
            .ToList();

        // Solo se juzga a los equipos que ya mandaron su seguridad (tienen el componente del firewall).
        var security = new List<SecurityRow>();
        foreach (var machine in components.Where(c => c.Type is "antivirus" or "firewall").GroupBy(c => new { c.Id, c.Name, c.Branch }))
        {
            var key = machine.Key;
            var antivirus = machine.Where(c => c.Type == "antivirus").ToList();
            if (antivirus.Count == 0 && machine.Any(c => c.Type == "firewall"))
                security.Add(new SecurityRow(key.Id, key.Name, key.Branch, "sin_antivirus", null));
            else if (antivirus.Count > 0 && antivirus.All(a => a.HealthStatus != HealthStatus.Healthy))
            {
                var best = antivirus.OrderBy(a => a.HealthStatus == HealthStatus.Warning ? 0 : 1).First();
                security.Add(new SecurityRow(key.Id, key.Name, key.Branch,
                    best.HealthStatus == HealthStatus.Warning ? "antivirus_desactualizado" : "antivirus_desactivado", best.Description));
            }
            if (machine.Any(c => c.Type == "firewall" && c.HealthStatus != HealthStatus.Healthy))
                security.Add(new SecurityRow(key.Id, key.Name, key.Branch, "firewall_desactivado", null));
        }

        return new DiskSecurityReport(lowDisks, diskHealth, security.OrderBy(s => s.MachineName).ToList());
    }

    // ------------------------------------------------------------------ rendimiento (avanzado)

    public async Task<PerformanceReport> PerformanceAsync(ReportFilter filter, CancellationToken ct)
    {
        var (start, end) = Range(filter);
        var machines = await MachinesAsync(filter, ct);
        var hourly = db.HourlyMetrics.AsNoTracking()
            .Where(h => h.Hour >= start && h.Hour < end && machines.Select(m => m.Id).Contains(h.MachineId));

        var perMachine = await hourly
            .GroupBy(h => h.MachineId)
            .Select(g => new
            {
                g.Key,
                Cpu = g.Average(h => h.CpuAverage), CpuPeak = g.Max(h => h.CpuAverage),
                Ram = g.Average(h => h.RamAverage), RamPeak = g.Max(h => h.RamAverage),
                Disk = g.Max(h => h.DiskMax), Hours = g.Count(),
            })
            .ToDictionaryAsync(g => g.Key, ct);
        var names = await machines.Select(m => new { m.Id, m.Name, Branch = m.Branch!.Name }).ToListAsync(ct);

        static decimal? Round(decimal? value) => value is decimal v ? Math.Round(v, 1) : null;
        var rows = names
            .Where(m => perMachine.ContainsKey(m.Id))
            .Select(m =>
            {
                var s = perMachine[m.Id];
                var advice = new List<string>();
                if (s.Ram >= 85) advice.Add("ampliar_ram");
                if (s.Cpu >= 80) advice.Add("revisar_cpu");
                if (s.Disk >= 90) advice.Add("liberar_disco");
                return new PerformanceRow(m.Id, m.Name, m.Branch, Round(s.Cpu), Round(s.CpuPeak), Round(s.Ram), Round(s.RamPeak), Round(s.Disk), s.Hours, advice);
            })
            .OrderByDescending(r => r.Advice.Count).ThenByDescending(r => r.RamAverage ?? 0).ThenByDescending(r => r.CpuAverage ?? 0)
            .ToList();

        var trend = (await hourly
                .GroupBy(h => h.Hour.Date)
                .Select(g => new { g.Key, Cpu = g.Average(h => h.CpuAverage), Ram = g.Average(h => h.RamAverage) })
                .ToListAsync(ct))
            .OrderBy(d => d.Key)
            .Select(d => new DailyUsage(DateOnly.FromDateTime(d.Key), Round(d.Cpu), Round(d.Ram)))
            .ToList();

        return new PerformanceReport(trend, rows);
    }

    // ------------------------------------------------------------------ software (avanzado)

    public async Task<SoftwareReport> SoftwareAsync(ReportFilter filter, string? search, CancellationToken ct)
    {
        var machines = await MachinesAsync(filter, ct);
        var software = db.InstalledSoftware.AsNoTracking().Where(s => machines.Select(m => m.Id).Contains(s.MachineId));
        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
            software = software.Where(s => s.Name.Contains(term));

        var versions = await software
            .GroupBy(s => new { s.Name, s.Version })
            .Select(g => new { g.Key.Name, g.Key.Version, Publisher = g.Max(s => s.Publisher), Machines = g.Select(s => s.MachineId).Distinct().Count() })
            .ToListAsync(ct);
        var perProgram = await software
            .GroupBy(s => s.Name)
            .Select(g => new { g.Key, Machines = g.Select(s => s.MachineId).Distinct().Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Machines, ct);

        var rows = versions
            .GroupBy(v => v.Name)
            .Select(g => new SoftwareRow(
                g.Key, g.Select(v => v.Publisher).FirstOrDefault(p => p is not null), perProgram.GetValueOrDefault(g.Key),
                g.OrderByDescending(v => v.Machines).Select(v => new CountItem(v.Version ?? "Sin versión", v.Machines)).ToList()))
            .OrderByDescending(r => r.Machines).ThenBy(r => r.Name)
            .ToList();

        var holders = string.IsNullOrEmpty(term)
            ? []
            : await software
                .Join(machines, s => s.MachineId, m => m.Id, (s, m) => new { s, m })
                .OrderBy(x => x.s.Name).ThenBy(x => x.m.Name)
                .Take(500)
                .Select(x => new SoftwareHolder(x.m.Id, x.m.Name, x.m.Branch!.Name, x.s.Version, x.s.InstalledOn))
                .ToListAsync(ct);

        return new SoftwareReport(rows.Count, rows.Take(1000).ToList(), holders);
    }

    // ------------------------------------------------------------------ tickets (avanzado)

    public async Task<TicketReport> TicketsAsync(ReportFilter filter, CancellationToken ct)
    {
        var (start, end) = Range(filter);
        var machines = await MachinesAsync(filter, ct, includeRetired: true);
        var narrowed = await NarrowedAsync(filter, ct);
        // Los tickets sin equipo solo se cuentan cuando se ve toda la empresa.
        var tickets = db.Tickets.AsNoTracking()
            .Where(t => t.MachineId != null ? machines.Select(m => m.Id).Contains(t.MachineId.Value) : !narrowed);

        var inPeriod = await tickets
            .Where(t => (t.CreatedAt >= start && t.CreatedAt < end) || (t.ClosedAt >= start && t.ClosedAt < end))
            .Select(t => new
            {
                t.CreatedAt, t.ClosedAt, t.Priority, t.Status,
                Technician = t.AssignedToId == null ? null : db.Users.Where(u => u.Id == t.AssignedToId).Select(u => u.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var open = await tickets.CountAsync(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed, ct);

        var created = inPeriod.Where(t => t.CreatedAt >= start && t.CreatedAt < end).ToList();
        var closed = inPeriod.Where(t => t.ClosedAt >= start && t.ClosedAt < end).ToList();

        var technicians = created
            .Where(t => t.Technician is not null)
            .GroupBy(t => t.Technician!)
            .Select(g => new TechnicianLoad(
                g.Key, g.Count(), g.Count(t => t.ClosedAt is not null),
                Hours(g.Where(t => t.ClosedAt is not null).Select(t => (t.ClosedAt!.Value - t.CreatedAt).TotalMinutes))))
            .OrderByDescending(t => t.Assigned)
            .ToList();

        var today = DateOnly.FromDateTime(Now);
        var maintenance = await db.Maintenances.AsNoTracking()
            .Where(m => m.ScheduledDate >= filter.From && m.ScheduledDate <= filter.To && machines.Select(x => x.Id).Contains(m.MachineId))
            .Select(m => new { m.ScheduledDate, m.CompletedDate })
            .ToListAsync(ct);

        return new TicketReport(
            created.Count, closed.Count, open,
            Hours(closed.Select(t => (t.ClosedAt!.Value - t.CreatedAt).TotalMinutes)),
            created.GroupBy(t => Text(t.Priority)).Select(g => new CountItem(g.Key, g.Count())).ToList(),
            technicians,
            new MaintenanceSummary(
                maintenance.Count(m => m.CompletedDate is not null),
                maintenance.Count(m => m.CompletedDate is null && m.ScheduledDate >= today),
                maintenance.Count(m => m.CompletedDate is null && m.ScheduledDate < today)));
    }

    // ------------------------------------------------------------------ accesos remotos (avanzado)

    public async Task<RemoteReport> RemoteAsync(ReportFilter filter, CancellationToken ct)
    {
        var (start, end) = Range(filter);
        var machines = await MachinesAsync(filter, ct, includeRetired: true);
        var sessions = await db.RemoteSessions.AsNoTracking()
            .Where(s => s.StartedAt >= start && s.StartedAt < end)
            .Join(machines, s => s.MachineId, m => m.Id, (s, m) => new
            {
                s.Id, s.StartedAt, s.EndedAt, s.Type, s.ViewOnly, s.Reason, MachineId = m.Id, MachineName = m.Name, Branch = m.Branch!.Name,
                UserName = db.Users.Where(u => u.Id == s.UserId).Select(u => u.Name).FirstOrDefault() ?? "—",
            })
            .OrderByDescending(s => s.StartedAt)
            .ToListAsync(ct);

        int Minutes(DateTime from, DateTime? to) => Math.Max(0, (int)Math.Round(((to ?? Now) - from).TotalMinutes));

        var rows = sessions.Select(s => new RemoteSessionRow(
            s.Id, s.StartedAt, s.EndedAt, Minutes(s.StartedAt, s.EndedAt), s.UserName, s.MachineId, s.MachineName, s.Branch,
            Text(s.Type), s.ViewOnly, s.Reason)).ToList();

        return new RemoteReport(
            rows.Count,
            rows.Sum(r => r.Minutes),
            rows.GroupBy(r => r.UserName).Select(g => new RemoteTechnician(g.Key, g.Count(), g.Sum(r => r.Minutes)))
                .OrderByDescending(t => t.Sessions).ToList(),
            rows.Take(500).ToList());
    }
}

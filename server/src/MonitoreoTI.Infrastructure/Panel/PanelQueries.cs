using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Entities.Inventory;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Conversions;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Lecturas del panel y la app. La empresa ya la filtran Row-Level Security y el filtro global de EF Core
/// (contexto fijado desde el token); aquí se agrega el alcance por sucursal del usuario.
/// </summary>
public sealed class PanelQueries(MonitoreoDbContext db, ICurrentUser user, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public static string Text<TEnum>(TEnum value) where TEnum : struct, Enum => EnumMemberConverter<TEnum>.ToProvider(value);

    /// <summary>Máquinas visibles para el usuario: de su empresa, de sus sucursales y no dadas de baja.</summary>
    private async Task<IQueryable<Machine>> VisibleMachinesAsync(CancellationToken ct, bool includeRetired = false)
    {
        var query = db.Machines.AsNoTracking();
        if (!includeRetired)
            query = query.Where(m => m.Status != MachineStatus.Retired);

        var branches = await user.AllowedBranchIdsAsync(ct);
        return branches is null ? query : query.Where(m => branches.Contains(m.BranchId));
    }

    private async Task<IQueryable<Branch>> VisibleBranchesAsync(CancellationToken ct)
    {
        var branches = await user.AllowedBranchIdsAsync(ct);
        var query = db.Branches.AsNoTracking();
        return branches is null ? query : query.Where(b => branches.Contains(b.Id));
    }

    /// <summary>La máquina, si existe y el usuario puede verla; null en cualquier otro caso (también si es de otra empresa).</summary>
    public async Task<bool> CanSeeMachineAsync(int machineId, CancellationToken ct) =>
        await (await VisibleMachinesAsync(ct, includeRetired: true)).AnyAsync(m => m.Id == machineId, ct);

    /// <summary>
    /// Lo que identifica un código QR, si el usuario puede verlo: un equipo de sus sucursales, o (con red.ver) un
    /// gabinete, dispositivo de red o roseta de su empresa. Null en cualquier otro caso.
    /// </summary>
    public async Task<QrTarget?> ResolveQrAsync(Guid code, CancellationToken ct)
    {
        if (user.HasPermission("equipos.ver")
            && await (await VisibleMachinesAsync(ct, includeRetired: true)).Where(m => m.QrCode == code).Select(m => (int?)m.Id).FirstOrDefaultAsync(ct) is int machineId)
            return new QrTarget("maquina", machineId);
        if (!user.HasPermission("red.ver"))
            return null;
        if (await db.NetworkDevices.AsNoTracking().Where(d => d.QrCode == code).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct) is int deviceId)
            return new QrTarget("dispositivo", deviceId);
        if (await db.Racks.AsNoTracking().Where(r => r.QrCode == code).Select(r => (int?)r.Id).FirstOrDefaultAsync(ct) is int rackId)
            return new QrTarget("gabinete", rackId);
        if (await db.NetworkOutlets.AsNoTracking().Where(o => o.QrCode == code).Select(o => (int?)o.Id).FirstOrDefaultAsync(ct) is int outletId)
            return new QrTarget("roseta", outletId);
        return null;
    }

    private static async Task<StatusCounts> CountAsync(IQueryable<Machine> machines, CancellationToken ct)
    {
        var groups = await machines.GroupBy(m => m.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(MachineStatus s) => groups.FirstOrDefault(g => g.Key == s)?.Count ?? 0;
        return new StatusCounts(
            groups.Sum(g => g.Count), Of(MachineStatus.Healthy), Of(MachineStatus.Warning), Of(MachineStatus.Critical), Of(MachineStatus.Disconnected));
    }

    // ------------------------------------------------------------------ dashboard

    public async Task<DashboardData> DashboardAsync(CancellationToken ct)
    {
        var machines = await VisibleMachinesAsync(ct);
        var counts = await CountAsync(machines, ct);
        var weekAgo = Now.AddDays(-7);

        var kpis = new DashboardKpis(
            counts,
            await machines.CountAsync(m => m.CreatedAt >= weekAgo, ct),
            await machines.CountAsync(m => m.Status == MachineStatus.Critical
                && db.Tickets.Any(t => t.MachineId == m.Id && t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed), ct),
            await machines.CountAsync(m => m.Status == MachineStatus.Disconnected && m.LastReportAt < Now.AddHours(-24), ct));

        // Historial de 7 días. Hoy se calcula en vivo; los días anteriores salen de estado_diario
        // (con sucursales restringidas no hay foto por sucursal: se muestra solo hoy).
        var today = DateOnly.FromDateTime(Now);
        var history = new List<DailyHealth>();
        if (await user.AllowedBranchIdsAsync(ct) is null)
        {
            history = await db.DailyFleetStatus.AsNoTracking()
                .Where(d => d.Date >= today.AddDays(-6) && d.Date < today)
                .OrderBy(d => d.Date)
                .Select(d => new DailyHealth(d.Date, d.Healthy, d.Warning, d.Critical, d.Disconnected))
                .ToListAsync(ct);
        }
        history.Add(new DailyHealth(today, counts.Healthy, counts.Warning, counts.Critical, counts.Disconnected));

        var branchRows = await (await VisibleBranchesAsync(ct))
            .Where(b => b.IsActive)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Province,
                Statuses = machines.Where(m => m.BranchId == b.Id).Select(m => m.Status).ToList(),
            })
            .ToListAsync(ct);
        var branches = branchRows
            .Select(b => new BranchSummary(b.Id, b.Name, b.Province, Tally(b.Statuses)))
            .OrderByDescending(b => b.Counts.Critical).ThenByDescending(b => b.Counts.Warning).ThenByDescending(b => b.Counts.Total)
            .ToList();

        var visibleIds = machines.Select(m => m.Id);
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => a.Status != AlertStatus.Resolved && a.MachineId != null && visibleIds.Contains(a.MachineId.Value))
            .OrderByDescending(a => a.Level).ThenByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => new
            {
                a.Id,
                a.Message,
                a.Level,
                a.MachineId,
                Machine = db.Machines.Where(m => m.Id == a.MachineId).Select(m => new { m.Name, Branch = m.Branch!.Name }).FirstOrDefault(),
                a.CreatedAt,
            })
            .ToListAsync(ct);

        var since = Now.AddDays(-7);
        var problems = await machines
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Status,
                Events = db.ErrorEvents.Count(e => e.MachineId == m.Id && e.Timestamp >= since
                    && (e.Level == EventLevel.Error || e.Level == EventLevel.Critical)),
                Summary = db.ErrorEvents.Where(e => e.MachineId == m.Id && e.Timestamp >= since
                        && (e.Level == EventLevel.Error || e.Level == EventLevel.Critical))
                    .OrderByDescending(e => e.Timestamp)
                    .Select(e => e.SimpleMessage ?? e.Source).FirstOrDefault(),
                OpenAlert = db.Alerts.Where(a => a.MachineId == m.Id && a.Status != AlertStatus.Resolved)
                    .OrderByDescending(a => a.Level).Select(a => a.Message).FirstOrDefault(),
            })
            .Where(m => m.Events > 0 || m.OpenAlert != null)
            .OrderByDescending(m => m.Events)
            .Take(5)
            .ToListAsync(ct);

        return new DashboardData(
            kpis,
            history,
            branches,
            alerts.Select(a => new RecentAlert(a.Id, a.Message, Text(a.Level), a.MachineId, a.Machine?.Name, a.Machine?.Branch, a.CreatedAt)).ToList(),
            problems.Select(p => new ProblemMachine(p.Id, p.Name, Text(p.Status), p.OpenAlert ?? p.Summary ?? "Errores del sistema", p.Events)).ToList());
    }

    private static StatusCounts Tally(IReadOnlyCollection<MachineStatus> statuses) => new(
        statuses.Count,
        statuses.Count(s => s == MachineStatus.Healthy),
        statuses.Count(s => s == MachineStatus.Warning),
        statuses.Count(s => s == MachineStatus.Critical),
        statuses.Count(s => s == MachineStatus.Disconnected));

    // ------------------------------------------------------------------ lista de equipos

    public async Task<MachineList> MachinesAsync(MachineFilter filter, CancellationToken ct)
    {
        var visible = await VisibleMachinesAsync(ct);
        var query = visible;

        if (!string.IsNullOrWhiteSpace(filter.Query))
        {
            var q = filter.Query.Trim();
            var qr = Guid.TryParse(q, out var code) ? code : (Guid?)null;
            query = query.Where(m => m.Name.Contains(q) || (m.IpAddress != null && m.IpAddress.Contains(q))
                || (m.MacAddress != null && m.MacAddress.Contains(q)) || (qr != null && m.QrCode == qr));
        }
        if (filter.BranchId is int branchId)
            query = query.Where(m => m.BranchId == branchId);
        if (TryParseText<MachineType>(filter.Type, out var type))
            query = query.Where(m => m.Type == type);
        if (!string.IsNullOrWhiteSpace(filter.OperatingSystem))
            query = query.Where(m => m.OperatingSystem != null && m.OperatingSystem.Contains(filter.OperatingSystem));

        // Los conteos (chips) respetan los demás filtros pero no el de estado, para que se vea cuánto hay de cada uno.
        var counts = await CountAsync(query, ct);
        if (TryParseText<MachineStatus>(filter.Status, out var status))
            query = query.Where(m => m.Status == status);

        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var page = Math.Max(1, filter.Page);
        var total = await query.CountAsync(ct);

        var rows = await query
            // Primero lo que necesita atención: crítico, advertencia, desconectado, sano.
            .OrderBy(m => m.Status == MachineStatus.Critical ? 0 : m.Status == MachineStatus.Warning ? 1 : m.Status == MachineStatus.Disconnected ? 2 : 3)
            .ThenBy(m => m.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.OperatingSystem,
                m.Model,
                m.BranchId,
                Branch = m.Branch!.Name,
                m.Type,
                m.IpAddress,
                m.Status,
                m.LastReportAt,
                Metric = db.Metrics.Where(x => x.MachineId == m.Id).OrderByDescending(x => x.Timestamp)
                    .Select(x => new LatestMetrics(x.Timestamp, x.Cpu, x.Ram, x.Disk, x.Temperature)).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var systems = await visible.Where(m => m.OperatingSystem != null).Select(m => m.OperatingSystem!).Distinct().OrderBy(s => s).ToListAsync(ct);

        var items = rows.Select(m => new MachineListItem(
            m.Id, m.Name, m.OperatingSystem ?? m.Model, m.BranchId, m.Branch, Text(m.Type), m.IpAddress, Text(m.Status),
            // Un equipo desconectado no muestra métricas viejas como si fueran actuales.
            m.Status == MachineStatus.Disconnected ? null : m.Metric,
            m.LastReportAt)).ToList();

        return new MachineList(new PagedResult<MachineListItem>(items, total, page, pageSize), counts, systems);
    }

    // ------------------------------------------------------------------ ficha

    public async Task<MachineDetail?> MachineAsync(int id, CancellationToken ct)
    {
        var machine = await (await VisibleMachinesAsync(ct, includeRetired: true))
            .Where(m => m.Id == id)
            .Select(m => new
            {
                m,
                Branch = m.Branch!.Name,
                Department = m.Department != null ? m.Department.Name : null,
                AgentVersion = m.Agent != null ? m.Agent.Version : null,
                HasAgent = m.Agent != null,
                Collector = db.Machines.Where(c => c.Agent != null && c.Agent.Id == m.CollectorAgentId).Select(c => c.Name).FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (machine is null)
            return null;

        var m = machine.m;
        var metric = await db.Metrics.AsNoTracking().Where(x => x.MachineId == id).OrderByDescending(x => x.Timestamp)
            .Select(x => new LatestMetrics(x.Timestamp, x.Cpu, x.Ram, x.Disk, x.Temperature)).FirstOrDefaultAsync(ct);

        var components = await db.Components.AsNoTracking().Where(c => c.MachineId == id)
            .OrderBy(c => c.Type).ThenBy(c => c.Description)
            .Select(c => new { c.Id, c.Type, c.Description, c.Capacity, c.HealthStatus, c.Detail, c.UpdatedAt })
            .ToListAsync(ct);

        var problems = await db.Alerts.AsNoTracking().Where(a => a.MachineId == id && a.Status != AlertStatus.Resolved)
            .OrderByDescending(a => a.Level).ThenByDescending(a => a.CreatedAt)
            .Select(a => new { a.Id, a.Level, a.Message, a.Detail, a.Advice, a.Source, a.CreatedAt, a.Code })
            .ToListAsync(ct);

        var volumes = await db.Volumes.AsNoTracking().Where(v => v.MachineId == id)
            .OrderBy(v => v.Drive)
            .Select(v => new VolumeItem(v.Drive, v.Label, v.TotalGb, v.FreeGb, v.UpdatedAt))
            .ToListAsync(ct);

        var since = Now.AddDays(-7);
        var errors = await db.ErrorEvents.CountAsync(e => e.MachineId == id && e.Timestamp >= since
            && (e.Level == EventLevel.Error || e.Level == EventLevel.Critical), ct);

        return new MachineDetail(
            m.Id, m.QrCode, m.Name, Text(m.Type), Text(m.Status), m.Brand, m.Model, m.SerialNumber, m.OperatingSystem,
            m.IpAddress, m.MacAddress, m.AssignedTo, m.PurchaseDate, m.WarrantyUntil, m.BranchId, machine.Branch,
            m.DepartmentId, machine.Department, m.LastReportAt, m.CreatedAt, machine.AgentVersion, machine.HasAgent,
            m.Status == MachineStatus.Disconnected ? null : metric,
            components.Select(c => new ComponentItem(c.Id, c.Type, c.Description, c.Capacity, Text(c.HealthStatus), c.Detail, c.UpdatedAt)).ToList(),
            problems.Select(p => new OpenProblem(p.Id, Text(p.Level), p.Message, p.Detail, p.Advice, p.Source, p.CreatedAt, p.Code)).ToList(),
            errors,
            m.RamTotalMb,
            m.ProcessCount,
            volumes,
            m.SnmpVersion is null ? null
                : new PrinterSnmpInfo(m.SnmpVersion, m.SnmpUser, m.SnmpEncryptedCredential is not null, m.CollectorAgentId, machine.Collector,
                    m.LastSnmpPollAt, m.LastSnmpError));
    }

    public async Task<IReadOnlyList<MetricPoint>?> MetricsAsync(int machineId, MetricRange range, CancellationToken ct)
    {
        if (!await CanSeeMachineAsync(machineId, ct))
            return null;

        var (from, bucketMinutes) = range switch
        {
            MetricRange.LastHour => (Now.AddHours(-1), 0),
            MetricRange.LastDay => (Now.AddDays(-1), 5),
            _ => (Now.AddDays(-7), 60),
        };

        var raw = db.Metrics.AsNoTracking().Where(x => x.MachineId == machineId && x.Timestamp >= from);
        if (bucketMinutes == 0)
        {
            return await raw.OrderBy(x => x.Timestamp)
                .Select(x => new MetricPoint(x.Timestamp, x.Cpu, x.Ram, x.Disk, x.Temperature))
                .ToListAsync(ct);
        }

        // Promedios por intervalo (máximo para disco y temperatura: lo que importa es el pico).
        var rows = await raw.Select(x => new { x.Timestamp, x.Cpu, x.Ram, x.Disk, x.Temperature }).ToListAsync(ct);
        return rows
            .GroupBy(x => new DateTime(x.Timestamp.Ticks - x.Timestamp.Ticks % TimeSpan.FromMinutes(bucketMinutes).Ticks, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new MetricPoint(
                g.Key,
                Round(g.Average(x => x.Cpu)),
                Round(g.Average(x => x.Ram)),
                g.Max(x => x.Disk),
                g.Max(x => x.Temperature)))
            .ToList();
    }

    private static decimal? Round(decimal? value) => value is null ? null : Math.Round(value.Value, 1);

    public async Task<IReadOnlyList<SoftwareItem>?> SoftwareAsync(int machineId, CancellationToken ct) =>
        await CanSeeMachineAsync(machineId, ct)
            ? await db.InstalledSoftware.AsNoTracking().Where(s => s.MachineId == machineId).OrderBy(s => s.Name)
                .Select(s => new SoftwareItem(s.Name, s.Version, s.Publisher, s.InstalledOn)).ToListAsync(ct)
            : null;

    public async Task<IReadOnlyList<NetworkInterfaceItem>?> InterfacesAsync(int machineId, CancellationToken ct)
    {
        if (!await CanSeeMachineAsync(machineId, ct))
            return null;
        var rows = await db.NetworkInterfaces.AsNoTracking().Where(i => i.MachineId == machineId).OrderBy(i => i.Name).ToListAsync(ct);
        return rows.Select(i => new NetworkInterfaceItem(i.Name, i.MacAddress, i.IpAddress, Text(i.Type), i.SpeedMbps, i.UpdatedAt)).ToList();
    }

    public async Task<IReadOnlyList<ErrorEventItem>?> ErrorsAsync(int machineId, CancellationToken ct)
    {
        if (!await CanSeeMachineAsync(machineId, ct))
            return null;
        var rows = await db.ErrorEvents.AsNoTracking().Where(e => e.MachineId == machineId)
            .OrderByDescending(e => e.Timestamp).Take(200).ToListAsync(ct);
        return rows.Select(e => new ErrorEventItem(e.Id, e.Timestamp, Text(e.Level), e.Source, e.Code, e.Message, e.SimpleMessage)).ToList();
    }

    /// <summary>Procesos de la última foto que mandó el agente.</summary>
    public async Task<IReadOnlyList<ProcessItem>?> ProcessesAsync(int machineId, CancellationToken ct)
    {
        if (!await CanSeeMachineAsync(machineId, ct))
            return null;
        var last = await db.ProcessSnapshots.Where(p => p.MachineId == machineId).MaxAsync(p => (DateTime?)p.Timestamp, ct);
        if (last is null)
            return [];
        return await db.ProcessSnapshots.AsNoTracking().Where(p => p.MachineId == machineId && p.Timestamp == last)
            .OrderByDescending(p => p.Cpu).ThenByDescending(p => p.RamMb)
            .Select(p => new ProcessItem(p.Name, p.Pid, p.Cpu, p.RamMb, p.Timestamp)).ToListAsync(ct);
    }

    /// <summary>Servicios automáticos del equipo; primero los que se detuvieron solos.</summary>
    public async Task<IReadOnlyList<ServiceItem>?> ServicesAsync(int machineId, CancellationToken ct)
    {
        if (!await CanSeeMachineAsync(machineId, ct))
            return null;
        return await db.Services.AsNoTracking().Where(s => s.MachineId == machineId)
            .OrderBy(s => s.StoppedSince == null).ThenBy(s => s.State).ThenBy(s => s.DisplayName ?? s.Name)
            .Select(s => new ServiceItem(s.Name, s.DisplayName, s.State, s.StartMode, s.StoppedSince)).ToListAsync(ct);
    }

    /// <summary>Historial de cambios del equipo (auditoría).</summary>
    public async Task<IReadOnlyList<AuditItem>?> HistoryAsync(int machineId, CancellationToken ct)
    {
        if (!await CanSeeMachineAsync(machineId, ct))
            return null;
        var id = machineId.ToString();
        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.Entity == "maquinas" && a.EntityId == id)
            .OrderByDescending(a => a.Timestamp).Take(100)
            .Select(a => new AuditItem(a.Id, a.Timestamp, a.Action,
                db.Users.Where(u => u.Id == a.UserId).Select(u => u.Name).FirstOrDefault(), a.Details))
            .ToListAsync(ct);
    }

    // ------------------------------------------------------------------ sucursales

    public async Task<IReadOnlyList<BranchItem>> BranchesAsync(CancellationToken ct)
    {
        var machines = await VisibleMachinesAsync(ct);
        var rows = await (await VisibleBranchesAsync(ct))
            .OrderBy(b => b.Name)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Province,
                b.Address,
                b.ManagerId,
                Manager = db.Users.Where(u => u.Id == b.ManagerId).Select(u => u.Name).FirstOrDefault(),
                b.IsActive,
                Statuses = machines.Where(m => m.BranchId == b.Id).Select(m => m.Status).ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(b => new BranchItem(b.Id, b.Name, b.Province, b.Address, b.ManagerId, b.Manager, b.IsActive, Tally(b.Statuses))).ToList();
    }

    public async Task<BranchItem?> BranchAsync(int id, CancellationToken ct) =>
        (await BranchesAsync(ct)).FirstOrDefault(b => b.Id == id);

    // No se llama TryParse: Minimal API tomaría a PanelQueries por un tipo enlazable desde la URL.
    private static bool TryParseText<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            result = EnumMemberConverter<TEnum>.FromProvider(value.Trim());
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

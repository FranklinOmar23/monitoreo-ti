namespace MonitoreoTI.Application.Panel;

/// <summary>Usuario de la petición, tomado del token ya validado.</summary>
public interface ICurrentUser
{
    int UserId { get; }
    int CompanyId { get; }
    string? Role { get; }
    bool HasPermission(string key);

    /// <summary>
    /// Sucursales que puede ver. null = todas. El rol Administrador ve todas; los demás roles, si tienen
    /// sucursales asignadas (usuario_sucursal), solo esas (sección 7.1).
    /// </summary>
    Task<IReadOnlyCollection<int>?> AllowedBranchIdsAsync(CancellationToken cancellationToken);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>A qué apunta un código QR (monitoreoti://q/{codigo}): Type es maquina, gabinete, dispositivo o roseta.</summary>
public sealed record QrTarget(string Type, int Id);

// ------------------------------------------------------------------ dashboard (W-04)

public sealed record StatusCounts(int Total, int Healthy, int Warning, int Critical, int Disconnected)
{
    public int Online => Total - Disconnected;
}

public sealed record DashboardKpis(
    StatusCounts Counts,
    int RegisteredThisWeek,
    int CriticalWithOpenTicket,
    int DisconnectedOver24h);

public sealed record DailyHealth(DateOnly Date, int Healthy, int Warning, int Critical, int Disconnected);

public sealed record BranchSummary(int Id, string Name, string? Province, StatusCounts Counts);

public sealed record RecentAlert(long Id, string Message, string Level, int? MachineId, string? MachineName, string? BranchName, DateTime CreatedAt);

public sealed record ProblemMachine(int Id, string Name, string Status, string Summary, int Events);

public sealed record DashboardData(
    DashboardKpis Kpis,
    IReadOnlyList<DailyHealth> FleetHealth,
    IReadOnlyList<BranchSummary> Branches,
    IReadOnlyList<RecentAlert> RecentAlerts,
    IReadOnlyList<ProblemMachine> TopProblems);

// ------------------------------------------------------------------ equipos (W-05)

public sealed record MachineFilter(
    string? Query = null,
    int? BranchId = null,
    string? Type = null,
    string? Status = null,
    string? OperatingSystem = null,
    int Page = 1,
    int PageSize = 50);

public sealed record LatestMetrics(DateTime At, decimal? Cpu, decimal? Ram, decimal? Disk, decimal? Temperature);

public sealed record MachineListItem(
    int Id,
    string Name,
    string? Detail,
    int BranchId,
    string BranchName,
    string Type,
    string? IpAddress,
    string Status,
    LatestMetrics? Metrics,
    DateTime? LastReportAt);

public sealed record MachineList(PagedResult<MachineListItem> Page, StatusCounts Counts, IReadOnlyList<string> OperatingSystems);

// ------------------------------------------------------------------ ficha (W-06 a W-12)

public sealed record MachineDetail(
    int Id,
    Guid QrCode,
    string Name,
    string Type,
    string Status,
    string? Brand,
    string? Model,
    string? SerialNumber,
    string? OperatingSystem,
    string? IpAddress,
    string? MacAddress,
    string? AssignedTo,
    DateOnly? PurchaseDate,
    DateOnly? WarrantyUntil,
    int BranchId,
    string BranchName,
    int? DepartmentId,
    string? DepartmentName,
    DateTime? LastReportAt,
    DateTime CreatedAt,
    string? AgentVersion,
    bool HasAgent,
    LatestMetrics? Metrics,
    IReadOnlyList<ComponentItem> Components,
    IReadOnlyList<OpenProblem> Problems,
    int OpenErrorCount,
    int? RamTotalMb,
    int? ProcessCount,
    IReadOnlyList<VolumeItem> Volumes,
    /// <summary>Impresoras consultadas por SNMP (nunca incluye la comunidad ni las claves).</summary>
    PrinterSnmpInfo? Snmp = null);

public sealed record PrinterSnmpInfo(
    string Version, string? User, bool HasCredential, int? CollectorId, string? CollectorName, DateTime? LastPollAt, string? LastError);

public sealed record ComponentItem(int Id, string Type, string? Description, string? Capacity, string Health, string? Detail, DateTime UpdatedAt);

/// <summary>Problema abierto del equipo con su explicación y qué hacer (motor de alertas).</summary>
/// <param name="Code">Qué problema es ("servicio:Spooler"): el panel ofrece la acción que corresponde.</param>
public sealed record OpenProblem(long Id, string Level, string Message, string? Detail, string? Advice, string? Source, DateTime CreatedAt, string? Code = null);

public sealed record VolumeItem(string Drive, string? Label, decimal TotalGb, decimal FreeGb, DateTime UpdatedAt);

public sealed record ServiceItem(string Name, string? DisplayName, string State, string StartMode, DateTime? StoppedSince);

public sealed record MetricPoint(DateTime At, decimal? Cpu, decimal? Ram, decimal? Disk, decimal? Temperature);

public sealed record SoftwareItem(string Name, string? Version, string? Publisher, DateOnly? InstalledOn);

public sealed record NetworkInterfaceItem(string Name, string MacAddress, string? IpAddress, string Type, int? SpeedMbps, DateTime UpdatedAt);

public sealed record ErrorEventItem(long Id, DateTime At, string Level, string? Source, int? Code, string? Message, string? PlainMessage);

public sealed record ProcessItem(string Name, int? Pid, decimal? Cpu, decimal? RamMb, DateTime At);

public sealed record AuditItem(long Id, DateTime At, string Action, string? UserName, string? Details);

/// <summary>Rangos de la gráfica de la ficha: puntos crudos (1 h), cada 5 min (24 h) o cada hora (7 días).</summary>
public enum MetricRange
{
    LastHour,
    LastDay,
    LastWeek,
}

// ------------------------------------------------------------------ sucursales (W-15 a W-17)

public sealed record BranchItem(
    int Id, string Name, string? Province, string? Address, int? ManagerId, string? ManagerName, bool IsActive, StatusCounts Counts);

// ------------------------------------------------------------------ escritura: auditoría

/// <summary>Registro de auditoría de escrituras (sección 7.6), con usuario, empresa e IP de la petición.</summary>
public interface IAuditTrail
{
    Task RecordAsync(string action, string entity, string? entityId, object? before, object? after, CancellationToken cancellationToken);
}

// ------------------------------------------------------------------ tiempo real

/// <summary>Avisos en vivo al panel y la app (SignalR). La implementación vive en el API.</summary>
public interface IRealtimeNotifier
{
    Task MachineStatusChangedAsync(int companyId, int branchId, int machineId, string status, CancellationToken cancellationToken);
    Task MetricsReceivedAsync(int companyId, int machineId, MetricPoint point, CancellationToken cancellationToken);

    /// <summary>Una alerta se abrió (isNew) o cambió de nivel o estado.</summary>
    Task AlertChangedAsync(int companyId, int branchId, long alertId, int? machineId, bool isNew, CancellationToken cancellationToken);

    /// <summary>Un ticket se creó o cambió (estado, asignación, comentario, adjunto).</summary>
    Task TicketChangedAsync(int companyId, int branchId, int ticketId, int? machineId, CancellationToken cancellationToken);

    /// <summary>Un puerto (o, con portId null, el dispositivo de red) cambió de estado.</summary>
    Task PortStatusChangedAsync(int companyId, int branchId, int deviceId, int? portId, string status, CancellationToken cancellationToken);

    /// <summary>Un comando o script cambió de estado (enviado, ejecutando, terminado...).</summary>
    Task CommandChangedAsync(int companyId, int branchId, long commandId, Guid? batchId, int machineId, string status, CancellationToken cancellationToken);
}

using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Application.Reports;

// Reportes (W-35). Básicos en todos los planes; los avanzados (rendimiento, software, tickets y accesos remotos)
// solo con el módulo reportes_avanzados, validado en el servidor (decisión 69).

/// <summary>Periodo (fechas inclusive, en UTC) y filtros comunes a todos los reportes.</summary>
public sealed record ReportFilter(DateOnly From, DateOnly To, int? BranchId = null, int? DepartmentId = null);

/// <summary>Qué reportes puede abrir la empresa.</summary>
public sealed record ReportsInfo(bool Advanced);

public sealed record CountItem(string Key, int Count);

// ------------------------------------------------------------------ inventario

public sealed record InventoryRow(
    int Id, string Name, string Type, string BranchName, string? DepartmentName, string? AssignedTo, string? OperatingSystem,
    string? Processor, int? RamGb, string? Disks, string? Brand, string? Model, string? SerialNumber, string? IpAddress,
    string? MacAddress, string Status, DateOnly? WarrantyUntil, DateTime CreatedAt, DateTime? LastReportAt);

public sealed record InventoryReport(int Total, IReadOnlyList<CountItem> ByType, IReadOnlyList<CountItem> ByOperatingSystem, IReadOnlyList<InventoryRow> Rows);

// ------------------------------------------------------------------ disponibilidad

/// <summary>Horas con datos frente a horas del periodo (desde el alta del equipo si fue después).</summary>
public sealed record AvailabilityRow(int Id, string Name, string BranchName, int HoursOnline, int HoursInPeriod, decimal Percent, DateTime? LastReportAt);

/// <summary>Days viene de la foto diaria de toda la empresa: vacío si el usuario o el filtro limitan las sucursales.</summary>
public sealed record AvailabilityReport(decimal AveragePercent, IReadOnlyList<DailyHealth> Days, IReadOnlyList<AvailabilityRow> Machines);

// ------------------------------------------------------------------ alertas

public sealed record DailyAlerts(DateOnly Date, int Critical, int Warning);

public sealed record AlertMachine(int Id, string Name, string BranchName, int Count, int Critical);

public sealed record AlertReport(
    int Total, int Critical, int Warning, int Open, double? AverageResolutionHours,
    IReadOnlyList<DailyAlerts> PerDay, IReadOnlyList<CountItem> ByCode, IReadOnlyList<CountItem> ByBranch, IReadOnlyList<AlertMachine> TopMachines);

// ------------------------------------------------------------------ discos y seguridad

public sealed record LowDiskRow(int MachineId, string MachineName, string BranchName, string Drive, decimal TotalGb, decimal FreeGb, decimal FreePercent);

public sealed record DiskHealthRow(int MachineId, string MachineName, string BranchName, string? Disk, string Health, string? Detail);

/// <summary>Issue: sin_antivirus, antivirus_desactivado, antivirus_desactualizado o firewall_desactivado.</summary>
public sealed record SecurityRow(int MachineId, string MachineName, string BranchName, string Issue, string? Detail);

public sealed record DiskSecurityReport(IReadOnlyList<LowDiskRow> LowDisks, IReadOnlyList<DiskHealthRow> DiskHealth, IReadOnlyList<SecurityRow> Security);

// ------------------------------------------------------------------ rendimiento (avanzado)

/// <summary>Advice: ampliar_ram, revisar_cpu o liberar_disco; null si está bien.</summary>
public sealed record PerformanceRow(
    int Id, string Name, string BranchName, decimal? CpuAverage, decimal? CpuPeak, decimal? RamAverage, decimal? RamPeak,
    decimal? DiskMax, int Hours, IReadOnlyList<string> Advice);

public sealed record DailyUsage(DateOnly Date, decimal? Cpu, decimal? Ram);

public sealed record PerformanceReport(IReadOnlyList<DailyUsage> Trend, IReadOnlyList<PerformanceRow> Machines);

// ------------------------------------------------------------------ software (avanzado)

public sealed record SoftwareRow(string Name, string? Publisher, int Machines, IReadOnlyList<CountItem> Versions);

public sealed record SoftwareHolder(int MachineId, string MachineName, string BranchName, string? Version, DateOnly? InstalledOn);

/// <summary>Holders solo cuando se busca un programa (hasta 500).</summary>
public sealed record SoftwareReport(int Programs, IReadOnlyList<SoftwareRow> Rows, IReadOnlyList<SoftwareHolder> Holders);

// ------------------------------------------------------------------ tickets (avanzado)

public sealed record TechnicianLoad(string Name, int Assigned, int Closed, double? AverageResolutionHours);

public sealed record MaintenanceSummary(int Done, int Pending, int Overdue);

public sealed record TicketReport(
    int Created, int Closed, int Open, double? AverageResolutionHours, IReadOnlyList<CountItem> ByPriority,
    IReadOnlyList<TechnicianLoad> Technicians, MaintenanceSummary Maintenance);

// ------------------------------------------------------------------ accesos remotos (avanzado)

public sealed record RemoteTechnician(string Name, int Sessions, int Minutes);

public sealed record RemoteSessionRow(
    long Id, DateTime StartedAt, DateTime? EndedAt, int Minutes, string UserName, int MachineId, string MachineName, string BranchName,
    string Type, bool ViewOnly, string? Reason);

/// <summary>Rows: las 500 más recientes del periodo.</summary>
public sealed record RemoteReport(int Sessions, int Minutes, IReadOnlyList<RemoteTechnician> Technicians, IReadOnlyList<RemoteSessionRow> Rows);

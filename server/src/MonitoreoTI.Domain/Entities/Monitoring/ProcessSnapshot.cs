using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Foto de un proceso en un momento dado. Tabla: procesos_snapshot.</summary>
public class ProcessSnapshot : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Pid { get; set; }

    /// <summary>Porcentaje.</summary>
    public decimal? Cpu { get; set; }
    public decimal? RamMb { get; set; }
}

using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Resumen por hora (se guarda 1 año). Tabla: metricas_hora.</summary>
public class HourlyMetric : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public DateTime Hour { get; set; }
    public decimal? CpuAverage { get; set; }
    public decimal? RamAverage { get; set; }
    public decimal? DiskMax { get; set; }
    public decimal? TemperatureMax { get; set; }
}

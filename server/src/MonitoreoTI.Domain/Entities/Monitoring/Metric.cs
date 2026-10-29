using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Muestra de salud de una máquina; se guarda el detalle de 30 días. Tabla: metricas.</summary>
public class Metric : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>Porcentaje.</summary>
    public decimal? Cpu { get; set; }

    /// <summary>Porcentaje.</summary>
    public decimal? Ram { get; set; }

    /// <summary>Porcentaje del disco más lleno.</summary>
    public decimal? Disk { get; set; }

    /// <summary>°C.</summary>
    public decimal? Temperature { get; set; }
}

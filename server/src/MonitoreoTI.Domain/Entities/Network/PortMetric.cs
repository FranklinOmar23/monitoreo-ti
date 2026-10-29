using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Contadores de tráfico y errores de un puerto. Tabla: metricas_puerto.</summary>
public class PortMetric : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int PortId { get; set; }
    public DateTime Timestamp { get; set; }
    public long? BytesIn { get; set; }
    public long? BytesOut { get; set; }
    public long? ErrorsIn { get; set; }
    public long? ErrorsOut { get; set; }
    public long? Discards { get; set; }
}

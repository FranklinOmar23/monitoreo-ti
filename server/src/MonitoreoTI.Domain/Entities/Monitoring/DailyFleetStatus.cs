using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Foto diaria del semáforo de la flota de una empresa. Tabla: estado_diario.</summary>
public class DailyFleetStatus : ITenantEntity
{
    public int CompanyId { get; set; }
    public DateOnly Date { get; set; }
    public int Healthy { get; set; }
    public int Warning { get; set; }
    public int Critical { get; set; }
    public int Disconnected { get; set; }
    public DateTime UpdatedAt { get; set; }
}

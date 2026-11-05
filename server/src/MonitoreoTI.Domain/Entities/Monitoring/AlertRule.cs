using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Regla de alerta. Tabla: reglas_alerta.</summary>
public class AlertRule : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>null = aplica a toda la empresa.</summary>
    public int? BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public AlertMetric Metric { get; set; }
    public ComparisonOperator Operator { get; set; } = ComparisonOperator.GreaterThan;
    public decimal? WarningThreshold { get; set; }
    public decimal? CriticalThreshold { get; set; }

    /// <summary>Minutos que debe sostenerse la condición.</summary>
    public int DurationMinutes { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Servicio detenido: Centinela intenta iniciarlo solo (como máximo 3 veces al día por servicio).</summary>
    public bool AutoRepair { get; set; }

    /// <summary>Quien activó la reparación automática: a su nombre van los comandos que se mandan solos.</summary>
    public int? AutoRepairUserId { get; set; }
}

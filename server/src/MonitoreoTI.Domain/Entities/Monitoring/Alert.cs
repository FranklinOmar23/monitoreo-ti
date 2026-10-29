using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Alerta de una máquina, de un dispositivo de red o de un puerto. Tabla: alertas.</summary>
public class Alert : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int? MachineId { get; set; }
    public int? DeviceId { get; set; }
    public int? PortId { get; set; }
    public int? RuleId { get; set; }
    public int? HandledById { get; set; }
    public AlertLevel Level { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.New;
    public string Message { get; set; } = string.Empty;

    /// <summary>Tipo de problema y su clave (ej. "disco_lleno:C:"): hay una sola alerta abierta por código.</summary>
    public string? Code { get; set; }

    /// <summary>Explicación en lenguaje simple.</summary>
    public string? Detail { get; set; }

    /// <summary>Qué debe hacer el técnico.</summary>
    public string? Advice { get; set; }

    /// <summary>De dónde sale el dato (ej. "SMART 5 · Reallocated Sector Count").</summary>
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

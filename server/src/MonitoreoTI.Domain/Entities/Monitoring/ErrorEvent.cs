using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>Evento del registro de Windows. Tabla: eventos_error.</summary>
public class ErrorEvent : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>Ej. 'Microsoft-Windows-Kernel-Power'.</summary>
    public string? Source { get; set; }

    /// <summary>Ej. 41.</summary>
    public int? Code { get; set; }
    public EventLevel Level { get; set; }
    public string? Message { get; set; }

    /// <summary>Explicación en lenguaje simple.</summary>
    public string? SimpleMessage { get; set; }
}

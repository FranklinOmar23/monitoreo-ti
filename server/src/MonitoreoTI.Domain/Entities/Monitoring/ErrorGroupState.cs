using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Monitoring;

/// <summary>
/// Lo que un técnico decidió sobre un grupo de errores de Windows (mismo origen y código), decisión 91.
/// Tabla: errores_estado. Sin equipo: vale para toda la empresa (solo "ignorado").
/// </summary>
public class ErrorGroupState : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int? MachineId { get; set; }
    public string? Source { get; set; }
    public int? Code { get; set; }

    /// <summary>true: ignorado; false: resuelto hasta <see cref="At"/> (si vuelve a ocurrir, reaparece).</summary>
    public bool IsIgnored { get; set; }
    public string? Note { get; set; }
    public int? UserId { get; set; }
    public DateTime At { get; set; }
}

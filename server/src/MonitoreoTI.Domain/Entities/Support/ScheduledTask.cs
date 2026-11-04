using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>
/// Script de la biblioteca que se ejecuta solo (6.5). Las horas son de República Dominicana (UTC-4); NextRunAt y
/// LastRunAt se guardan en UTC. Tabla: tareas_programadas.
/// </summary>
public class ScheduledTask : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ScriptId { get; set; }
    public int CreatedById { get; set; }

    /// <summary>equipos, sucursal o todos.</summary>
    public string Target { get; set; } = "equipos";
    public int? BranchId { get; set; }

    /// <summary>Ids de equipos separados por coma (Target = equipos).</summary>
    public string? MachineIds { get; set; }

    /// <summary>una_vez, diaria, semanal o mensual.</summary>
    public string Frequency { get; set; } = "diaria";
    public TimeOnly Time { get; set; }
    public DateOnly? Date { get; set; }

    /// <summary>Semanal: lunes = 1, martes = 2, miércoles = 4 ... domingo = 64.</summary>
    public byte? WeekDays { get; set; }
    public byte? MonthDay { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public bool IsActive { get; set; } = true;
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public Guid? LastBatchId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Backups;

/// <summary>
/// Trabajo de respaldo de un servidor (decisión 82): qué se respalda (bases SQL Server o carpetas), cómo (en caliente o
/// en frío, completo cada vez o solo lo nuevo), dónde (disco externo, carpeta de red o disco local) y cuándo. Lo ejecuta
/// el agente del servidor en su horario; aquí queda además el último estado para el panel y las alertas.
/// Tabla: respaldo_trabajos.
/// </summary>
public class BackupJob : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>sql o carpetas.</summary>
    public string Source { get; set; } = "sql";

    /// <summary>Instancia de SQL Server (MSSQLSERVER, SQLEXPRESS…); null para carpetas.</summary>
    public string? SqlInstance { get; set; }

    /// <summary>JSON: nombres de las bases o rutas de las carpetas.</summary>
    public string Items { get; set; } = "[]";

    /// <summary>caliente o frio.</summary>
    public string Mode { get; set; } = "caliente";

    /// <summary>JSON: servicios que se detienen en frío.</summary>
    public string StopServices { get; set; } = "[]";
    public int ColdMaxMinutes { get; set; } = 60;

    /// <summary>completo (cada vez) o incremental (solo lo nuevo, con una copia completa cada FullEveryDays días).</summary>
    public string Strategy { get; set; } = "incremental";
    public int FullEveryDays { get; set; } = 7;

    /// <summary>disco_externo, red o local.</summary>
    public string DestinationType { get; set; } = "disco_externo";

    /// <summary>Red: \\servidor\carpeta. Local: D:\Respaldos. Disco externo: la carpeta dentro del disco.</summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>JSON: [{serie, nombre}] de los discos externos que sirven (rotación).</summary>
    public string Disks { get; set; } = "[]";
    public bool EjectAfter { get; set; }
    public string? NetworkUser { get; set; }

    /// <summary>Clave de la carpeta de red, cifrada por la aplicación; nunca en texto plano.</summary>
    public byte[]? NetworkEncryptedPassword { get; set; }

    /// <summary>diario o semanal.</summary>
    public string Frequency { get; set; } = "diario";

    /// <summary>Semanal: días separados por coma, 0 = domingo.</summary>
    public string? WeekDays { get; set; }

    /// <summary>Hora local del servidor, HH:mm.</summary>
    public string Time { get; set; } = "23:00";
    public int KeepDaily { get; set; } = 7;
    public int KeepMonthly { get; set; } = 3;
    public bool Verify { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Lo último que informó el agente (para el panel y las alertas).
    public DateTime? LastRunAt { get; set; }

    /// <summary>en_curso, correcto, advertencia o error.</summary>
    public string? LastStatus { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? NextRunAt { get; set; }

    /// <summary>Desde cuándo no hay ninguno de sus discos conectado (aviso antes de la hora).</summary>
    public DateTime? DiskMissingSince { get; set; }

    /// <summary>Servicios que no volvieron a arrancar después de un respaldo en frío.</summary>
    public string? ServicesNotRestarted { get; set; }
}

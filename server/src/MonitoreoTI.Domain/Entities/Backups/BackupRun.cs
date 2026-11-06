using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Backups;

/// <summary>Una ejecución de un trabajo de respaldo, como la informó el agente. Tabla: respaldo_ejecuciones.</summary>
public class BackupRun : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int JobId { get; set; }
    public int MachineId { get; set; }

    /// <summary>El id que le dio el agente: une el aviso de inicio con el del final.</summary>
    public Guid RunUid { get; set; }

    /// <summary>programado o manual.</summary>
    public string Trigger { get; set; } = "programado";
    public int? RequestedById { get; set; }

    /// <summary>en_curso, correcto, advertencia o error.</summary>
    public string Status { get; set; } = "en_curso";

    /// <summary>completo, incremental o diferencial; null si falló antes de saberlo.</summary>
    public string? Kind { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Lo que se copió de verdad (en incremental, solo lo nuevo).</summary>
    public long BytesCopied { get; set; }

    /// <summary>El tamaño de todo lo respaldado.</summary>
    public long BytesTotal { get; set; }
    public int? Files { get; set; }

    /// <summary>Dónde quedó: "Disco WD Elements (E:)", \\nas\respaldos…</summary>
    public string? Destination { get; set; }

    /// <summary>Carpeta del respaldo dentro del destino (para restaurar a mano).</summary>
    public string? SetName { get; set; }
    public string? Detail { get; set; }
}

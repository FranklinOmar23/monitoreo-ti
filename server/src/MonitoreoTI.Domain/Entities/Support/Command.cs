using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Comando, script o acción enviado a una máquina. Tabla: comandos.</summary>
public class Command : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public int UserId { get; set; }
    public int? ScriptId { get; set; }
    public CommandType Type { get; set; }

    /// <summary>Una ejecución en varios equipos comparte lote.</summary>
    public Guid? BatchId { get; set; }
    public ScriptLanguage? Language { get; set; }
    public string? Content { get; set; }
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Si no se entrega antes de esta hora, pasa a expirado (y el agente tampoco lo acepta después).</summary>
    public DateTime? ExpiresAt { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public string? Result { get; set; }
    public int? ExitCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

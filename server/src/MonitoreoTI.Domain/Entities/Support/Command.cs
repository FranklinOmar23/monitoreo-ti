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
    public string? Content { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Pending;
    public string? Result { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

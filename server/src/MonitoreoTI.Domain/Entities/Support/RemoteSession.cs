using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Sesión remota (escritorio, terminal o archivos). Tabla: sesiones_remotas.</summary>
public class RemoteSession : ITenantEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public int UserId { get; set; }
    public RemoteSessionType Type { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? SourceIp { get; set; }
}

using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>
/// Registro de auditoría. CompanyId null = acción del superadministrador o del sistema
/// fuera de una empresa. Tabla: auditoria.
/// </summary>
public class AuditLog : IOptionalTenantEntity
{
    public long Id { get; set; }
    public int? CompanyId { get; set; }

    /// <summary>null = acción del sistema.</summary>
    public int? UserId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Entity { get; set; } = string.Empty;
    public string? EntityId { get; set; }

    /// <summary>JSON con valores antes/después.</summary>
    public string? Details { get; set; }
    public string? SourceIp { get; set; }
}

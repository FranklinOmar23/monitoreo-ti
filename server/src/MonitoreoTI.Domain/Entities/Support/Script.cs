using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Support;

/// <summary>Script de la biblioteca. Tabla: scripts.</summary>
public class Script : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int CreatedById { get; set; }
    public string Name { get; set; } = string.Empty;
    public ScriptLanguage Language { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>Firma ECDSA de la empresa; el agente rechaza lo no firmado.</summary>
    public string? Signature { get; set; }
    public DateTime CreatedAt { get; set; }
}

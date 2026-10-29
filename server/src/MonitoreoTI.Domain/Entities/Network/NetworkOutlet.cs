using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Roseta de pared o escritorio. Tabla: puntos_red.</summary>
public class NetworkOutlet : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BranchId { get; set; }

    /// <summary>Ej. 'P-2-15'.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Ej. 'Contabilidad, escritorio 3'.</summary>
    public string? Location { get; set; }
    public Guid QrCode { get; set; }
}

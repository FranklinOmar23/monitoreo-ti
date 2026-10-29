using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Cable documentado: une dos puertos (PortBId) o un puerto con una roseta (OutletId),
/// exactamente uno de los dos. Tabla: cableado.
/// </summary>
public class Cable : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int PortAId { get; set; }
    public int? PortBId { get; set; }
    public int? OutletId { get; set; }
    public CableType? CableType { get; set; }
    public string? Color { get; set; }
    public string? Label { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
}

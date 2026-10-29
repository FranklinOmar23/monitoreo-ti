using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Sala o data center de una sucursal. Tabla: salas.</summary>
public class Room : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Location { get; set; }

    public ICollection<Rack> Racks { get; set; } = new List<Rack>();
}

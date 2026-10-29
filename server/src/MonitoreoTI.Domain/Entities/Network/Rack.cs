using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>Gabinete. Tabla: gabinetes.</summary>
public class Rack : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int RoomId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid QrCode { get; set; }
    public byte HeightUnits { get; set; } = 42;

    /// <summary>Fila y posición en el plano de la sala.</summary>
    public string? Row { get; set; }
    public int? Position { get; set; }
    public bool IsActive { get; set; } = true;

    public Room? Room { get; set; }
    public ICollection<NetworkDevice> Devices { get; set; } = new List<NetworkDevice>();
}

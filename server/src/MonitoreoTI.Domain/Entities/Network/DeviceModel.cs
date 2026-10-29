using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Modelo del catálogo; sus puertos se crean al agregar un dispositivo.
/// CompanyId null = catálogo base compartido. Tabla: modelos_dispositivo.
/// </summary>
public class DeviceModel : IOptionalTenantEntity
{
    public int Id { get; set; }
    public int? CompanyId { get; set; }
    public string Brand { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public DeviceType Type { get; set; }
    public byte HeightUnits { get; set; } = 1;
    public int PortCount { get; set; }

    /// <summary>Ej. '48 RJ45 + 4 SFP+'.</summary>
    public string? PortDescription { get; set; }

    /// <summary>true = tiene SNMP; false = pasivo (patch panel).</summary>
    public bool IsActiveDevice { get; set; } = true;
}

using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Unidad de disco con su espacio. Tabla: volumenes.</summary>
public class Volume : ITenantEntity
{
    public int CompanyId { get; set; }
    public int MachineId { get; set; }

    /// <summary>Letra de unidad, ej. "C:".</summary>
    public string Drive { get; set; } = string.Empty;
    public string? Label { get; set; }
    public decimal TotalGb { get; set; }
    public decimal FreeGb { get; set; }
    public DateTime UpdatedAt { get; set; }
}

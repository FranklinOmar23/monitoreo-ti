using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Servicio de Windows de la máquina. Tabla: servicios.</summary>
public class WindowsService : ITenantEntity
{
    public int CompanyId { get; set; }
    public int MachineId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    /// <summary>en_ejecucion, detenido u otro.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>automatico, automatico_retrasado, manual o deshabilitado.</summary>
    public string StartMode { get; set; } = string.Empty;

    /// <summary>Se marca cuando un servicio automático que estaba en ejecución se detiene.</summary>
    public DateTime? StoppedSince { get; set; }
    public DateTime UpdatedAt { get; set; }
}

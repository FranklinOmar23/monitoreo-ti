using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>
/// Departamento de una sucursal (decisión 79): "Contabilidad" de Santo Domingo y "Contabilidad" de Santiago son dos
/// departamentos distintos. Un equipo solo puede estar en un departamento de su propia sucursal. Tabla: departamentos.
/// </summary>
public class Department : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Usuario de la empresa a cargo del departamento (opcional).</summary>
    public int? ResponsibleUserId { get; set; }
}

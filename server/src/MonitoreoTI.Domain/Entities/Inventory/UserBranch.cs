using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Sucursal que cubre un técnico. Tabla: usuario_sucursal.</summary>
public class UserBranch : ITenantEntity
{
    public int CompanyId { get; set; }
    public int UserId { get; set; }
    public int BranchId { get; set; }

    public Branch? Branch { get; set; }
}

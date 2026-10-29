using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>Agrupa permisos. CompanyId null = rol del sistema. Tabla: roles.</summary>
public class Role : IOptionalTenantEntity
{
    public int Id { get; set; }
    public int? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}

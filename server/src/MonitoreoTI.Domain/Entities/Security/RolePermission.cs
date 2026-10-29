namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>Tabla: rol_permiso.</summary>
public class RolePermission
{
    public int RoleId { get; set; }
    public int PermissionId { get; set; }

    public Permission? Permission { get; set; }
}

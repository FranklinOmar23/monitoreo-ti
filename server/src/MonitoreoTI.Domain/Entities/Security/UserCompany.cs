namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>
/// Membresía de un usuario en una empresa con su rol. Tabla: usuario_empresa.
/// Se lee durante el login, antes de conocer la empresa: no tiene filtro por empresa.
/// </summary>
public class UserCompany
{
    public int UserId { get; set; }
    public int CompanyId { get; set; }
    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public Role? Role { get; set; }
}

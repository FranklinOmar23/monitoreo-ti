namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>
/// Usuario global: pertenece a una o varias empresas mediante UserCompany. Tabla: usuarios.
/// Nunca se borra; se desactiva con IsActive = false.
/// </summary>
public class User
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Secreto 2FA cifrado por la aplicación.</summary>
    public string? TotpSecret { get; set; }

    /// <summary>Proveedor del sistema; no pertenece a ninguna empresa.</summary>
    public bool IsSuperAdmin { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Recibe por correo las alertas críticas de lo que gestiona.</summary>
    public bool NotifyByEmail { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>Último código 2FA válido: el acceso remoto y los scripts exigen que sea reciente (decisión 52).</summary>
    public DateTime? LastTwoFactorAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<UserCompany> Companies { get; set; } = new List<UserCompany>();
}

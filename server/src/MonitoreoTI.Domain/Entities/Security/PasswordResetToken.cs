namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>
/// Enlace de "recuperar contraseña" (solo el hash). Un solo uso y vence en minutos.
/// Tabla: tokens_restablecimiento. Se usa antes de iniciar sesión: no tiene empresa.
/// </summary>
public class PasswordResetToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public string? IpAddress { get; set; }
}

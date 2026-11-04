namespace MonitoreoTI.Domain.Entities.Security;

/// <summary>
/// Refresh token rotativo (solo el hash). CompanyId null = sesión del superadministrador.
/// Tabla: refresh_tokens. Se lee antes de conocer la empresa: no tiene filtro por empresa.
/// </summary>
public class RefreshToken
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int? CompanyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public long? ReplacedById { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>Equipo o programa desde el que se inició la sesión; pasa a cada renovación.</summary>
    public string? Device { get; set; }
}

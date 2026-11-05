using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Inventory;

/// <summary>Token de registro de agentes de una sucursal. Tabla: tokens_registro.</summary>
public class EnrollmentToken : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int BranchId { get; set; }
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// El token cifrado por la aplicación, para volver a mostrar el comando de instalación (solo a quien puede
    /// generar instaladores, y queda en la auditoría). Los tokens creados antes no lo tienen.
    /// </summary>
    public byte[]? EncryptedToken { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Etiqueta para reconocerlo en el panel.</summary>
    public string? Name { get; set; }

    /// <summary>null = sin límite.</summary>
    public int? MaxUses { get; set; }
    public int Uses { get; set; }

    /// <summary>Los equipos que registra quedan pendientes de aprobación.</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>Revocado desde el panel: ya no registra equipos.</summary>
    public DateTime? RevokedAt { get; set; }
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }
}

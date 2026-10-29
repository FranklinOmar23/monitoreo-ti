using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Companies;

/// <summary>Acceso temporal del superadministrador, autorizado por la empresa. Tabla: accesos_soporte.</summary>
public class SupportAccess : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int SuperAdminId { get; set; }
    public int AuthorizedById { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

using MonitoreoTI.Domain.Common;
using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Companies;

/// <summary>Tabla: suscripciones.</summary>
public class Subscription : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int PlanId { get; set; }
    public DateOnly StartDate { get; set; }

    /// <summary>null = vigente sin fecha de fin.</summary>
    public DateOnly? EndDate { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    public Plan? Plan { get; set; }
}

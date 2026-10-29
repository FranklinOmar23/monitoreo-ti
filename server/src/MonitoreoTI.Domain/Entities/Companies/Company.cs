using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Companies;

/// <summary>Empresa cliente (tenant). Tabla: empresas.</summary>
public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? TaxId { get; set; }
    public string? ContactEmail { get; set; }
    public string? Phone { get; set; }
    public int PlanId { get; set; }
    public CompanyStatus Status { get; set; } = CompanyStatus.Active;
    public DateTime CreatedAt { get; set; }

    public Plan? Plan { get; set; }
}

namespace MonitoreoTI.Domain.Entities.Companies;

/// <summary>Plan comercial con sus límites. Tabla global: planes.</summary>
public class Plan
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>null = sin límite.</summary>
    public int? MaxAgents { get; set; }
    public int? MaxBranches { get; set; }
    public int? MaxUsers { get; set; }
    public decimal MonthlyPrice { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<PlanModule> Modules { get; set; } = new List<PlanModule>();
}

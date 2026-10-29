using MonitoreoTI.Domain.Enums;

namespace MonitoreoTI.Domain.Entities.Companies;

/// <summary>Módulo incluido en un plan. Tabla global: plan_modulos.</summary>
public class PlanModule
{
    public int PlanId { get; set; }
    public ModuleKey Module { get; set; }
}

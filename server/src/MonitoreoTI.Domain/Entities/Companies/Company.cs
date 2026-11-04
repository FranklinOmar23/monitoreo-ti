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

    /// <summary>Los agentes se actualizan solos a la versión publicada en el servidor (6.6).</summary>
    public bool AutoUpdateAgents { get; set; } = true;

    /// <summary>El usuario del equipo debe aceptar antes de que un técnico vea o controle su pantalla (decisión 52).</summary>
    public bool AskRemoteConsent { get; set; }

    /// <summary>Logo del panel (W-40): PNG, JPEG o WebP de hasta 256 KB. Nunca SVG (puede llevar código).</summary>
    public byte[]? Logo { get; set; }

    /// <summary>image/png, image/jpeg o image/webp; null sin logo.</summary>
    public string? LogoType { get; set; }

    /// <summary>Color de la empresa (#rrggbb en minúsculas) que reemplaza al principal del panel; null = el de Centinela.</summary>
    public string? BrandColor { get; set; }
    public DateTime CreatedAt { get; set; }

    public Plan? Plan { get; set; }
}

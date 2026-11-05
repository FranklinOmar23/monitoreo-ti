using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Network;

/// <summary>
/// Instalación remota del agente en una IP de la subred (decisión 77): la hace el recolector de la subred con una
/// cuenta del dominio que se escribe en el panel y no se guarda. Tabla: instalaciones_remotas.
/// </summary>
public class RemoteInstall : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public int SubnetId { get; set; }
    public string Ip { get; set; } = string.Empty;

    /// <summary>Agente que hace la instalación.</summary>
    public int CollectorAgentId { get; set; }
    public int UserId { get; set; }

    /// <summary>DOMINIO\usuario con el que se instaló (nunca la clave).</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>Token de registro de un solo uso creado para este equipo.</summary>
    public int? EnrollmentTokenId { get; set; }

    /// <summary>pendiente, conectando, copiando, instalando, instalado, registrado, error u omitido.</summary>
    public string Status { get; set; } = "pendiente";
    public string? Detail { get; set; }

    /// <summary>Sistema del equipo según el recolector ("Windows 10 (10.0.19045) 64 bits").</summary>
    public string? OperatingSystem { get; set; }

    /// <summary>El equipo que se registró con el token.</summary>
    public int? MachineId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

namespace MonitoreoTI.Application.Abstractions;

/// <summary>
/// Empresa del usuario o agente autenticado en el ámbito actual (petición o trabajo en segundo plano).
/// Lo usan el filtro global de EF Core, la asignación de empresa al insertar y el
/// contexto de sesión de SQL Server que activa Row-Level Security.
/// </summary>
public interface ITenantContext
{
    /// <summary>null = sin empresa (antes del login, o superadministrador).</summary>
    int? CompanyId { get; }

    /// <summary>Solo el portal del proveedor: salta el filtro por empresa.</summary>
    bool IsSuperAdmin { get; }
}

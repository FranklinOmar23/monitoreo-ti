namespace MonitoreoTI.Domain.Common;

/// <summary>
/// Entidad que pertenece a una sola empresa (columna empresa_id NOT NULL).
/// El servidor asigna CompanyId al insertar; nunca se toma de la petición.
/// </summary>
public interface ITenantEntity
{
    int CompanyId { get; set; }
}

/// <summary>
/// Entidad que puede ser global (CompanyId null = registro del sistema) o propia de una empresa.
/// </summary>
public interface IOptionalTenantEntity
{
    int? CompanyId { get; set; }
}

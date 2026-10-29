namespace MonitoreoTI.Application.Abstractions;

/// <summary>
/// Implementación con alcance por petición. Se fija una sola vez: el contexto de sesión de
/// SQL Server se marca de solo lectura, así que cambiar de empresa dentro del mismo ámbito
/// dejaría la conexión y el filtro de EF Core desalineados.
/// Los trabajos en segundo plano crean un ámbito nuevo por cada empresa que procesan.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public int? CompanyId { get; private set; }
    public bool IsSuperAdmin { get; private set; }
    public bool IsSet { get; private set; }

    public void SetCompany(int companyId)
    {
        if (companyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(companyId), "El id de empresa debe ser positivo.");

        EnsureNotSet();
        CompanyId = companyId;
        IsSet = true;
    }

    public void SetSuperAdmin()
    {
        EnsureNotSet();
        IsSuperAdmin = true;
        IsSet = true;
    }

    private void EnsureNotSet()
    {
        if (IsSet)
            throw new InvalidOperationException("El contexto de empresa ya fue fijado para este ámbito.");
    }
}

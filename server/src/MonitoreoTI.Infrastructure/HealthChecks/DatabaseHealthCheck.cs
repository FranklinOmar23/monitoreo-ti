using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.HealthChecks;

/// <summary>Verifica que SQL Server responde. No necesita contexto de empresa.</summary>
public sealed class DatabaseHealthCheck(MonitoreoDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("SQL Server responde.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("No se pudo conectar a SQL Server.", ex);
        }
    }
}

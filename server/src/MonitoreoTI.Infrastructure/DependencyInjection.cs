using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Infrastructure.Agents;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Persistence.Interceptors;

namespace MonitoreoTI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Un contexto de empresa por petición o por ámbito de trabajo en segundo plano.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSessionContextInterceptor>();

        services.AddDbContext<MonitoreoDbContext>((sp, options) =>
        {
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
            options.AddInterceptors(sp.GetRequiredService<TenantSessionContextInterceptor>());
        });

        services.AddScoped<IAgentEnrollmentService, AgentEnrollmentService>();
        services.AddSingleton<IAgentSessions, AgentSessions>();
        services.AddSingleton<AgentPresenceTracker>();

        return services;
    }
}

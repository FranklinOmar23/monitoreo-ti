using System.Threading.RateLimiting;
using FluentValidation;
using MonitoreoTI.Api.Remote;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Agents;
using MonitoreoTI.Infrastructure.Health;
using MonitoreoTI.Infrastructure.Network;
using MonitoreoTI.Infrastructure.Remote;
using MonitoreoTI.Workers;

namespace MonitoreoTI.Api.Agents;

public static class AgentGatewayExtensions
{
    public static IServiceCollection AddAgentGateway(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IValidator<EnrollRequest>, EnrollRequestValidator>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(configuration.GetSection("Agents:Gateway").Get<AgentGatewayOptions>() ?? new AgentGatewayOptions());
        services.AddSingleton(configuration.GetSection("Agents:Presence").Get<AgentPresenceOptions>() ?? new AgentPresenceOptions());
        services.AddSingleton(configuration.GetSection("Agents:Metrics").Get<MetricBatchOptions>() ?? new MetricBatchOptions());
        services.AddSingleton<MetricBatchWriter>();
        services.AddSingleton<IMetricSink>(sp => sp.GetRequiredService<MetricBatchWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<MetricBatchWriter>());
        services.AddSingleton<IAgentInventory, AgentInventory>();
        services.AddSingleton<IAgentMessageHandler, MetricsHandler>();
        services.AddSingleton<IAgentMessageHandler, HardwareInventoryHandler>();
        services.AddSingleton<IAgentMessageHandler, SoftwareInventoryHandler>();
        services.AddSingleton<IAgentMessageHandler, NetworkInterfacesHandler>();
        services.AddSingleton<IAgentHealthStore, AgentHealthStore>();
        services.AddSingleton<IAgentMessageHandler, VolumesHandler>();
        services.AddSingleton<IAgentMessageHandler, ProcessesHandler>();
        services.AddSingleton<IAgentMessageHandler, ServicesHandler>();
        services.AddSingleton<IAgentMessageHandler, EventsHandler>();
        services.AddSingleton<IAgentMessageHandler, DiskHealthHandler>();
        services.AddSingleton<IAgentMessageHandler, SecurityHandler>();

        // Recolector SNMP y correlación (Fase 4).
        services.AddSingleton<OuiDirectory>();
        services.AddSingleton<ISnmpCollectors, SnmpCollectors>();
        services.AddSingleton<IAgentMessageHandler, SnmpResultHandler>();
        services.AddSingleton<ICollectorConfigPublisher, CollectorConfigPublisher>();
        // Inventario de direcciones IP (decisión 72).
        services.AddSingleton<IIpSweeps, IpSweeps>();
        services.AddSingleton<IIpSweepDispatcher, IpSweepDispatcher>();
        services.AddSingleton<IAgentMessageHandler, IpSweepResultHandler>();
        services.AddHostedService<IpSweepScheduler>();
        services.AddSingleton<FortiGateApiTester>();
        services.AddSingleton<IFortiGateApiTester>(sp => sp.GetRequiredService<FortiGateApiTester>());
        services.AddSingleton<IAgentMessageHandler, ApiTestResultHandler>();
        services.AddSingleton<IFortiGateData, FortiGateData>();
        services.AddSingleton<IAgentMessageHandler, FortiGateDataHandler>();
        services.AddSingleton<IRemoteInstallDispatcher, RemoteInstallDispatcher>();
        services.AddSingleton<IRemoteInstallStatus, RemoteInstallStatus>();
        services.AddSingleton<IAgentMessageHandler, RemoteInstallStatusHandler>();
        services.AddSingleton<NetworkAlertEngine>();
        services.AddSingleton<AlertMailer>();

        // Comandos y scripts firmados (Fase 6).
        services.AddScoped<ICompanySigner, CompanySigner>();
        services.AddSingleton<AgentCommands>();
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<ICommandDispatcher>(sp => sp.GetRequiredService<CommandDispatcher>());
        services.AddSingleton<IAgentMessageHandler, CommandResultHandler>();
        services.AddSingleton<ScheduledTaskRunner>();
        services.AddSingleton<AgentReleases>();
        services.AddSingleton<AgentUpdater>();
        services.AddHostedService<CommandExpiryService>();

        // Relay de sesiones remotas (Fase 6.2).
        services.AddSingleton<RemoteSessionLog>();
        services.AddSingleton<IAgentMessageHandler, SessionActivityHandler>();
        services.AddSingleton<RemoteRelay>();
        services.AddSingleton<IRemoteRelay>(sp => sp.GetRequiredService<RemoteRelay>());

        // Motor de alertas (Fase 3).
        services.AddSingleton(configuration.GetSection("Alerts").Get<AlertEngineOptions>() ?? new AlertEngineOptions());
        services.AddSingleton<AlertEngine>();
        services.AddSingleton<AlertEngineService>();
        services.AddHostedService(sp => sp.GetRequiredService<AlertEngineService>());
        services.AddSingleton<AgentConnectionRegistry>();
        services.AddSingleton<AgentGateway>();
        services.AddHostedService<AgentPresenceService>();

        // El registro de agentes no tiene sesión: se limita por IP para frenar la adivinación de tokens.
        var registerLimit = configuration.GetValue("Agents:RegisterRateLimitPerMinute", 20);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AgentEndpoints.RegisterRateLimit, http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "desconocida",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = registerLimit, Window = TimeSpan.FromMinutes(1) }));
        });

        return services;
    }

    public static WebApplication UseAgentGateway(this WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.MapAgentEndpoints();
        app.Map("/agent/ws", (HttpContext http, AgentGateway gateway) => gateway.HandleAsync(http));
        app.MapGet(AgentUpdater.DownloadPath, AgentUpdater.DownloadAsync);
        app.Map("/relay/{id:long}", (long id, HttpContext http, RemoteRelay relay) => relay.HandleAsync(http, id));
        return app;
    }
}

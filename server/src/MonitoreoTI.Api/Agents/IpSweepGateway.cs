using System.Net.WebSockets;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Infrastructure.Network;

namespace MonitoreoTI.Api.Agents;

/// <summary>Manda "barrido_ip" a un recolector conectado (decisión 72).</summary>
public sealed class IpSweepDispatcher(AgentConnectionRegistry registry, ILogger<IpSweepDispatcher> logger) : IIpSweepDispatcher
{
    public async Task<bool> SendAsync(int companyId, int agentId, IpSweepRequest request, CancellationToken cancellationToken)
    {
        if (registry.Get(agentId) is not { } connection || connection.Identity.CompanyId != companyId)
            return false;
        try
        {
            await connection.SendAsync("barrido_ip", IpInventory.Message(request), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            logger.LogInformation("Agente {AgentId}: no se pudo pedir el barrido de la subred {SubnetId}.", agentId, request.SubredId);
            return false;
        }
    }
}

/// <summary>Resultado de un barrido de IP del recolector.</summary>
public sealed class IpSweepResultHandler(IIpSweeps sweeps, TimeProvider clock, ILogger<IpSweepResultHandler> logger)
    : TelemetryHandler<BarridoIpData>(logger)
{
    public override string Tipo => "barrido_ip_resultado";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, BarridoIpData data, CancellationToken cancellationToken) =>
        // La hora es la de llegada: el barrido dice cómo está la red ahora, no cuando salió de la cola del agente.
        sweeps.SaveResultAsync(identity, data, clock.GetUtcNow().UtcDateTime, cancellationToken);
}

/// <summary>
/// Barridos periódicos: cada pocos minutos revisa los recolectores conectados y les pide las subredes que ya toca barrer
/// (según las horas de cada una). Un recolector desconectado recibe lo suyo en la vuelta siguiente a su regreso.
/// </summary>
public sealed class IpSweepScheduler(
    AgentConnectionRegistry registry, IIpSweeps sweeps, TimeProvider clock, IConfiguration configuration, ILogger<IpSweepScheduler> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var every = TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue("Red:RevisarBarridosMinutos", 5), 1, 60));
        using var timer = new PeriodicTimer(every);
        do
        {
            foreach (var connection in registry.All())
            {
                try
                {
                    foreach (var request in await sweeps.DueAsync(connection.Identity, clock.GetUtcNow().UtcDateTime, stoppingToken))
                        await connection.SendAsync("barrido_ip", IpInventory.Message(request), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Base de datos caída o conexión cerrada: el pedido vence y se repite más tarde.
                    logger.LogWarning(ex, "Agente {AgentId}: no se pudieron pedir sus barridos de IP.", connection.Identity.AgentId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

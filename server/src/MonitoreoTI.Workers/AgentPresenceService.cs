using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Workers;

public sealed class AgentPresenceOptions
{
    /// <summary>Cada cuánto se guarda el último contacto y se busca a los agentes caídos.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Sin contacto durante este tiempo la máquina pasa a "desconectado" (sección 7.5: 3 minutos).</summary>
    public TimeSpan DisconnectAfter { get; set; } = TimeSpan.FromMinutes(3);
}

/// <summary>
/// Guarda en lote el último contacto de los agentes conectados y es el detector de desconexión.
/// Recorre las empresas una por una: cada una con su propio contexto, sin saltar Row-Level Security.
/// </summary>
public sealed class AgentPresenceService(
    IAgentSessions sessions,
    AgentPresenceTracker tracker,
    AgentPresenceOptions options,
    TimeProvider clock,
    ILogger<AgentPresenceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, clock);
        try
        {
            do
            {
                await TickAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
    }

    /// <summary>Un ciclo completo. Público para poder probarlo sin esperar el temporizador.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dirty = tracker.TakeDirty();
            if (dirty.Count > 0)
                await sessions.FlushLastSeenAsync(dirty, cancellationToken);

            var threshold = clock.GetUtcNow().UtcDateTime - options.DisconnectAfter;
            foreach (var companyId in await sessions.ListCompanyIdsAsync(cancellationToken))
            {
                var marked = await sessions.MarkStaleDisconnectedAsync(companyId, threshold, cancellationToken);
                await sessions.SnapshotDailyStatusAsync(companyId, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), cancellationToken);
                if (marked > 0)
                    logger.LogInformation("Empresa {CompanyId}: {Count} equipo(s) pasaron a desconectado.", companyId, marked);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un fallo de base de datos no debe detener el servicio: se reintenta en el siguiente ciclo.
            logger.LogError(ex, "Falló el ciclo de presencia de agentes.");
        }
    }
}

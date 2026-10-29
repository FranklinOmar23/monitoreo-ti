using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Infrastructure.Health;

namespace MonitoreoTI.Workers;

public sealed class AlertEngineOptions
{
    /// <summary>Cada cuánto se evalúan las reglas (las métricas llegan cada minuto).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Ejecuta el motor de alertas empresa por empresa, cada una con su propio contexto (sin saltar RLS).</summary>
public sealed class AlertEngineService(
    AlertEngine engine,
    IAgentSessions sessions,
    AlertEngineOptions options,
    TimeProvider clock,
    ILogger<AlertEngineService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
    }

    /// <summary>Un ciclo completo. Público para probarlo sin esperar el temporizador.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<int> companies;
        try
        {
            companies = await sessions.ListCompanyIdsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo listar las empresas para el motor de alertas.");
            return;
        }

        foreach (var companyId in companies)
        {
            try
            {
                var result = await engine.EvaluateCompanyAsync(companyId, cancellationToken);
                if (result.Opened + result.Resolved > 0)
                {
                    logger.LogInformation("Empresa {CompanyId}: {Opened} alerta(s) nueva(s), {Resolved} resuelta(s).",
                        companyId, result.Opened, result.Resolved);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una empresa con un problema no detiene la evaluación de las demás.
                logger.LogError(ex, "Falló el motor de alertas para la empresa {CompanyId}.", companyId);
            }
        }
    }
}

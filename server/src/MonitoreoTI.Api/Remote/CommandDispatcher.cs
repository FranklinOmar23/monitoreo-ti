using System.Net.WebSockets;
using MonitoreoTI.Api.Agents;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Remote;

namespace MonitoreoTI.Api.Remote;

/// <summary>
/// Entrega "ejecutar_comando" a los agentes conectados. Al que no está conectado se le entrega en su próxima
/// bienvenida (si no vence antes).
/// </summary>
public sealed class CommandDispatcher(AgentConnectionRegistry registry, AgentCommands commands, ILogger<CommandDispatcher> logger)
    : ICommandDispatcher
{
    public async Task DispatchAsync(int companyId, IReadOnlyCollection<long> commandIds, CancellationToken cancellationToken)
    {
        var machines = await commands.MachinesOfAsync(companyId, commandIds, cancellationToken);
        foreach (var machineId in machines)
        {
            if (registry.FindByMachine(companyId, machineId) is { } connection)
                await SendPendingAsync(connection, commandIds, cancellationToken);
        }
    }

    /// <summary>Firma y envía lo pendiente de ese agente (todo, o solo los ids indicados).</summary>
    public async Task SendPendingAsync(AgentConnection connection, IReadOnlyCollection<long>? ids, CancellationToken cancellationToken)
    {
        var signed = await commands.TakeForAgentAsync(connection.Identity, ids, cancellationToken);
        var sent = new List<long>();
        try
        {
            foreach (var c in signed)
            {
                await connection.SendAsync("ejecutar_comando", Message(c), cancellationToken);
                sent.Add(c.ComandoId);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Se cortó la conexión a mitad: lo no enviado vuelve a pendiente y sale en la próxima bienvenida.
            var unsent = signed.Select(c => c.ComandoId).Except(sent).ToList();
            await commands.ReturnToPendingAsync(connection.Identity, unsent, CancellationToken.None);
            logger.LogInformation("Agente {AgentId}: {Count} comando(s) quedan pendientes para la próxima conexión.", connection.Identity.AgentId, unsent.Count);
        }
    }

    public static object Message(SignedCommand c) => new
    {
        comando_id = c.ComandoId,
        maquina_id = c.MaquinaId,
        tipo = c.Tipo,
        lenguaje = c.Lenguaje,
        contenido = c.Contenido,
        timeout_s = c.TimeoutS,
        vence = c.Vence,
        firma = c.Firma,
    };
}

/// <summary>
/// Cada minuto: ejecuta las tareas programadas a las que les tocó, vence los comandos que no se entregaron a tiempo o que se quedaron sin final, y cierra las sesiones
/// remotas que quedaron abiertas en la base sin estar en el relay (reinicio del servidor).
/// </summary>
public sealed class CommandExpiryService(
    AgentCommands commands,
    ScheduledTaskRunner scheduler,
    AgentUpdater updater,
    RemoteSessionLog sessionLog,
    IRemoteRelay relay,
    MonitoreoTI.Application.Agents.IAgentSessions sessions,
    TimeProvider clock,
    ILogger<CommandExpiryService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
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

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<int> companies;
        try
        {
            companies = await sessions.ListCompanyIdsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo listar las empresas para vencer comandos.");
            return;
        }

        foreach (var companyId in companies)
        {
            try
            {
                var ran = await scheduler.RunDueAsync(companyId, cancellationToken);
                if (ran > 0)
                    logger.LogInformation("Empresa {CompanyId}: {Count} tarea(s) programada(s) ejecutada(s).", companyId, ran);
                var count = await commands.ExpireAsync(companyId, cancellationToken);
                if (count > 0)
                    logger.LogInformation("Empresa {CompanyId}: {Count} comando(s) vencidos o sin final.", companyId, count);
                // Siguiente tanda de la actualización automática del agente.
                await updater.RolloutAsync(companyId, onlyIfAutomatic: true, cancellationToken);
                var orphans = await sessionLog.CloseOrphansAsync(companyId, relay.IsActive, cancellationToken);
                if (orphans > 0)
                    logger.LogInformation("Empresa {CompanyId}: {Count} sesión(es) remota(s) huérfana(s) cerradas.", companyId, orphans);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló el vencimiento de comandos de la empresa {CompanyId}.", companyId);
            }
        }
    }
}

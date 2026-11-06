using System.Collections.Concurrent;
using System.Net.WebSockets;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Backups;

namespace MonitoreoTI.Api.Agents;

/// <summary>
/// Respaldos de servidores (decisión 82), lado del gateway: manda la configuración firmada y "Respaldar ahora", y
/// pregunta al agente (bases, servicios y discos; probar el destino) esperando su respuesta con el mismo id.
/// </summary>
public sealed class BackupAgentLink(AgentConnectionRegistry registry, IBackupStatusStore store, ILogger<BackupAgentLink> logger)
    : IBackupAgentLink
{
    public static readonly TimeSpan ExploreTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, (int AgentId, TaskCompletionSource<object> Reply)> _pending = new();

    private AgentConnection? Connection(int companyId, int agentId) =>
        registry.Get(agentId) is { } connection && connection.Identity.CompanyId == companyId ? connection : null;

    public bool IsConnected(int companyId, int agentId) => Connection(companyId, agentId) is not null;

    public async Task<bool> PublishAsync(int companyId, int agentId, CancellationToken cancellationToken)
    {
        if (Connection(companyId, agentId) is not { } connection)
            return false;
        return await SendConfigAsync(connection, cancellationToken);
    }

    /// <summary>Al conectarse el agente (y al cambiar un trabajo): sus trabajos, firmados con la llave de la empresa.</summary>
    public async Task<bool> SendConfigAsync(AgentConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var config = await store.SignedConfigAsync(connection.Identity, cancellationToken);
            if (config is null)
                return false;
            await connection.SendAsync("respaldos_config", config, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            logger.LogInformation("Agente {AgentId}: no se pudo mandar la configuración de respaldos; la recibirá al reconectarse.",
                connection.Identity.AgentId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un problema con los respaldos (por ejemplo, la base sin sus tablas) no debe cortar la conexión del agente.
            logger.LogError(ex, "Agente {AgentId}: no se pudo armar la configuración de respaldos.", connection.Identity.AgentId);
            return false;
        }
    }

    public async Task<bool> RunNowAsync(int companyId, int agentId, object signedOrder, CancellationToken cancellationToken)
    {
        if (Connection(companyId, agentId) is not { } connection)
            return false;
        try
        {
            await connection.SendAsync("respaldo_ahora", signedOrder, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            logger.LogInformation("Agente {AgentId}: no se pudo mandar Respaldar ahora.", agentId);
            return false;
        }
    }

    public async Task<RespaldoExploradoData?> ExploreAsync(int companyId, int agentId, CancellationToken cancellationToken) =>
        await AskAsync(companyId, agentId, "respaldo_explorar", id => new { solicitud_id = id }, ExploreTimeout, cancellationToken) as RespaldoExploradoData;

    public async Task<RespaldoPruebaData?> TestDestinationAsync(int companyId, int agentId, object destination, CancellationToken cancellationToken) =>
        await AskAsync(companyId, agentId, "respaldo_probar", id => new { solicitud_id = id, destino = destination }, TestTimeout, cancellationToken) as RespaldoPruebaData;

    private async Task<object?> AskAsync(int companyId, int agentId, string tipo, Func<string, object> body, TimeSpan timeout, CancellationToken ct)
    {
        if (Connection(companyId, agentId) is not { } connection)
            return null;
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (agentId, reply);
        try
        {
            await connection.SendAsync(tipo, body(id), ct);
            return await reply.Task.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or TimeoutException)
        {
            logger.LogInformation("Agente {AgentId}: {Tipo} no volvió ({Error}).", agentId, tipo, ex.GetType().Name);
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>La respuesta solo cuenta si viene del mismo agente al que se le preguntó.</summary>
    public void Complete(int agentId, string solicitudId, object result)
    {
        if (_pending.TryGetValue(solicitudId, out var pending) && pending.AgentId == agentId)
            pending.Reply.TrySetResult(result);
    }
}

public sealed class BackupExploredHandler(BackupAgentLink link, ILogger<BackupExploredHandler> logger)
    : TelemetryHandler<RespaldoExploradoData>(logger)
{
    public override string Tipo => "respaldo_explorado";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, RespaldoExploradoData data, CancellationToken cancellationToken)
    {
        link.Complete(identity.AgentId, data.SolicitudId, data);
        return Task.CompletedTask;
    }
}

public sealed class BackupTestedHandler(BackupAgentLink link, ILogger<BackupTestedHandler> logger)
    : TelemetryHandler<RespaldoPruebaData>(logger)
{
    public override string Tipo => "respaldo_prueba";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, RespaldoPruebaData data, CancellationToken cancellationToken)
    {
        link.Complete(identity.AgentId, data.SolicitudId, data);
        return Task.CompletedTask;
    }
}

public sealed class BackupRunHandler(IBackupStatusStore store, TimeProvider clock, ILogger<BackupRunHandler> logger)
    : TelemetryHandler<RespaldoEstadoData>(logger)
{
    public override string Tipo => "respaldo_estado";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, RespaldoEstadoData data, CancellationToken cancellationToken) =>
        store.SaveRunAsync(identity, data, clock.GetUtcNow().UtcDateTime, cancellationToken);
}

public sealed class BackupScheduleHandler(IBackupStatusStore store, TimeProvider clock, ILogger<BackupScheduleHandler> logger)
    : TelemetryHandler<RespaldosAgendaData>(logger)
{
    public override string Tipo => "respaldos_agenda";

    protected override Task HandleAsync(AgentIdentity identity, AgentEnvelope message, RespaldosAgendaData data, CancellationToken cancellationToken) =>
        store.SaveScheduleAsync(identity, data, clock.GetUtcNow().UtcDateTime, cancellationToken);
}

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Network;

namespace MonitoreoTI.Api.Agents;

/// <summary>Conexión WebSocket de un agente. Un WebSocket admite un solo envío a la vez, de ahí el semáforo.</summary>
public sealed class AgentConnection(AgentIdentity identity, WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public AgentIdentity Identity { get; } = identity;

    public async Task SendAsync(string tipo, object datos, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1,
            tipo,
            id = Guid.NewGuid().ToString(),
            ts = DateTimeOffset.UtcNow,
            datos,
        });

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // La conexión ya estaba rota: no hay nada más que cerrar.
        }
    }
}

/// <summary>Agentes conectados ahora mismo. Una conexión nueva del mismo agente reemplaza a la anterior.</summary>
public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<int, AgentConnection> _connections = new();

    public int Count => _connections.Count;

    public AgentConnection? Get(int agentId) => _connections.GetValueOrDefault(agentId);

    public IReadOnlyCollection<AgentConnection> All() => _connections.Values.ToList();

    /// <summary>La conexión del agente de ese equipo, si está conectado.</summary>
    public AgentConnection? FindByMachine(int companyId, int machineId) =>
        _connections.Values.FirstOrDefault(c => c.Identity.CompanyId == companyId && c.Identity.MachineId == machineId);

    /// <summary>Registra la conexión y devuelve la que reemplaza (a cerrar), si la había.</summary>
    public AgentConnection? Replace(AgentConnection connection)
    {
        AgentConnection? previous = null;
        _connections.AddOrUpdate(
            connection.Identity.AgentId,
            connection,
            (_, existing) =>
            {
                previous = existing;
                return connection;
            });
        return previous;
    }

    /// <summary>Quita la conexión; true solo si seguía siendo la vigente (no fue reemplazada).</summary>
    public bool Remove(AgentConnection connection) =>
        _connections.TryRemove(new KeyValuePair<int, AgentConnection>(connection.Identity.AgentId, connection));
}

/// <summary>
/// Manda la lista de dispositivos SNMP vigente a los recolectores conectados (config_actualizada); los que no están
/// conectados la reciben en la bienvenida al volver.
/// </summary>
public sealed class CollectorConfigPublisher(AgentConnectionRegistry registry, ISnmpCollectors collectors, ILogger<CollectorConfigPublisher> logger)
    : ICollectorConfigPublisher
{
    public async Task ConfigChangedAsync(int companyId, IEnumerable<int> agentIds, CancellationToken cancellationToken)
    {
        foreach (var agentId in agentIds.Distinct())
        {
            if (registry.Get(agentId) is not { } connection || connection.Identity.CompanyId != companyId)
                continue;
            try
            {
                var config = await collectors.ConfigAsync(connection.Identity, cancellationToken);
                await connection.SendAsync("config_actualizada", new { recolector = AgentGateway.CollectorMessage(config) }, cancellationToken);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
                logger.LogInformation("Agente {AgentId}: no se pudo avisar el cambio de dispositivos; lo recibirá al reconectarse.", agentId);
            }
        }
    }

    public async Task<bool> PollNowAsync(int companyId, int agentId, int deviceId, bool agentSupportsPollNow, CancellationToken cancellationToken)
    {
        if (registry.Get(agentId) is not { } connection || connection.Identity.CompanyId != companyId)
            return false;
        try
        {
            if (agentSupportsPollNow)
                await connection.SendAsync("sondear_ahora", new { dispositivo_id = deviceId }, cancellationToken);
            else
            {
                // Al recibir la lista, el agente reprograma sus sondeos y el primero sale a los pocos segundos.
                var config = await collectors.ConfigAsync(connection.Identity, cancellationToken);
                await connection.SendAsync("config_actualizada", new { recolector = AgentGateway.CollectorMessage(config) }, cancellationToken);
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException)
        {
            logger.LogInformation("Agente {AgentId}: no se pudo pedir el sondeo del dispositivo {DeviceId}.", agentId, deviceId);
            return false;
        }
    }
}

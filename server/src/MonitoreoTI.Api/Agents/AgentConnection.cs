using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using MonitoreoTI.Application.Agents;

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

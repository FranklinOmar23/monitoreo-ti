using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Api.Agents;

/// <summary>Procesa un tipo de mensaje del agente. Las fases siguientes agregan uno por cada tipo del protocolo.</summary>
public interface IAgentMessageHandler
{
    string Tipo { get; }
    Task HandleAsync(AgentIdentity identity, AgentEnvelope message, CancellationToken cancellationToken);
}

public sealed class AgentGatewayOptions
{
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>Tiempo máximo para recibir "hola" tras abrir el socket.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Sin ningún mensaje durante este tiempo se cierra la conexión (3 heartbeats de 30 s).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public int HeartbeatSeconds { get; set; } = 30;
    public int MetricsSeconds { get; set; } = 60;
}

/// <summary>
/// Gateway de agentes (/agent/ws): el agente abre la conexión hacia el servidor, nunca al revés.
/// Se autentica con su credencial permanente en la cabecera Authorization: Bearer.
/// </summary>
public sealed class AgentGateway(
    IAgentSessions sessions,
    AgentPresenceTracker presence,
    AgentConnectionRegistry registry,
    IEnumerable<IAgentMessageHandler> handlers,
    AgentGatewayOptions options,
    ILogger<AgentGateway> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, IAgentMessageHandler> _handlers = handlers.ToDictionary(h => h.Tipo);

    public async Task HandleAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var identity = await sessions.AuthenticateAsync(ReadBearer(http.Request.Headers.Authorization), http.RequestAborted);
        if (identity is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var connection = new AgentConnection(identity, socket);

        var replaced = registry.Replace(connection);
        if (replaced is not null)
            await replaced.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Reemplazada por una conexión nueva.");

        presence.Connected(identity);
        logger.LogInformation("Agente {AgentId} (empresa {CompanyId}) conectado.", identity.AgentId, identity.CompanyId);

        try
        {
            await RunAsync(connection, socket, http.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Corte de red o cierre del agente: es lo normal en una flota real.
        }
        finally
        {
            // Si otra conexión ya tomó su lugar, la presencia pertenece a esa.
            if (registry.Remove(connection))
                presence.Disconnected(identity.AgentId);
            logger.LogInformation("Agente {AgentId} desconectado.", identity.AgentId);
        }
    }

    private async Task RunAsync(AgentConnection connection, WebSocket socket, CancellationToken aborted)
    {
        var identity = connection.Identity;

        var hello = await ReceiveAsync(socket, options.HelloTimeout, aborted);
        if (hello is null || hello.Tipo != "hola" || !TryReadData<HolaData>(hello, out var hola))
        {
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Se esperaba el mensaje hola.");
            return;
        }

        await sessions.OnConnectedAsync(identity, hola, aborted);
        await connection.SendAsync("ack", new { id = hello.Id }, aborted);
        await connection.SendAsync(
            "bienvenida",
            new
            {
                heartbeat_s = options.HeartbeatSeconds,
                metricas_s = options.MetricsSeconds,
                es_recolector = identity.IsCollector,
            },
            aborted);

        while (socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, options.IdleTimeout, aborted);
            if (message is null)
                break;

            presence.Seen(identity.AgentId);

            if (message.Tipo != "heartbeat" && _handlers.TryGetValue(message.Tipo, out var handler))
            {
                try
                {
                    await handler.HandleAsync(identity, message, aborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not WebSocketException)
                {
                    // Fallo del servidor (base de datos caída, cola llena): sin ack el agente conserva el mensaje
                    // en su cola local y lo reenvía. La conexión sigue viva para los demás mensajes.
                    logger.LogError(ex, "Agente {AgentId}: falló el procesamiento de {Tipo}; no se confirma.", identity.AgentId, message.Tipo);
                    continue;
                }
            }

            // Se confirma todo mensaje válido: el agente reintenta desde su cola solo lo que no recibe ack.
            await connection.SendAsync("ack", new { id = message.Id }, aborted);
        }

        await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Hasta luego.");
    }

    /// <summary>
    /// Lee un mensaje completo. null si el agente cerró, expiró el tiempo o el mensaje es inválido
    /// (en esos casos el llamador cierra la conexión).
    /// </summary>
    private async Task<AgentEnvelope?> ReceiveAsync(WebSocket socket, TimeSpan timeout, CancellationToken aborted)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeoutSource.CancelAfter(timeout);

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(chunk, timeoutSource.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                if (buffer.Length + result.Count > AgentGatewayOptions.MaxMessageBytes)
                {
                    logger.LogWarning("Mensaje de agente demasiado grande; se cierra la conexión.");
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Mensaje demasiado grande.", CancellationToken.None);
                    return null;
                }

                buffer.Write(chunk, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType != WebSocketMessageType.Text)
                    return null;

                return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            }
        }
        catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
        {
            logger.LogInformation("Conexión de agente cerrada por inactividad.");
            return null;
        }
    }

    private AgentEnvelope? Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<AgentEnvelope>(json, JsonOptions);
            if (envelope is null || envelope.V != 1 || string.IsNullOrWhiteSpace(envelope.Tipo)
                || string.IsNullOrWhiteSpace(envelope.Id) || envelope.Id.Length > 64)
                return null;
            return envelope;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadData<T>(AgentEnvelope envelope, out T data) where T : class
    {
        try
        {
            data = envelope.Datos.Deserialize<T>(JsonOptions)!;
            return data is not null;
        }
        catch (JsonException)
        {
            data = null!;
            return false;
        }
    }

    private static string? ReadBearer(StringValues header)
    {
        var value = header.ToString();
        const string prefix = "Bearer ";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..].Trim() : null;
    }
}

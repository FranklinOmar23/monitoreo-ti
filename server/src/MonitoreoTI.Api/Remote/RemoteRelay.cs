using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonitoreoTI.Api.Agents;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Remote;

namespace MonitoreoTI.Api.Remote;

/// <summary>
/// Relay de sesiones remotas (/relay/{id}, sección 10.1): el panel y el agente se conectan aquí, cada uno con su
/// token de un solo uso (primer mensaje, nunca en la URL), y el relay reenvía los marcos tal cual entre ambos.
/// Al panel solo le habla el relay con mensajes {"relay": "conectado" | "cerrada"}; lo demás viene del agente.
/// Ninguno necesita IP pública. No interpreta ni registra lo que pasa por él.
/// </summary>
public sealed class RemoteRelay(
    AgentConnectionRegistry agents, RemoteSessionLog log, TimeProvider clock, ILogger<RemoteRelay> logger) : IRemoteRelay
{
    private const int FrameChunk = 64 * 1024;
    private static readonly TimeSpan TokenMessageTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<long, RelaySession> _sessions = new();

    public bool IsActive(long sessionId) => _sessions.ContainsKey(sessionId);

    public async Task<(string PanelToken, DateTime ExpiresAt)?> OpenAsync(RelayOpening opening, CancellationToken cancellationToken)
    {
        if (agents.FindByMachine(opening.CompanyId, opening.MachineId) is not { } connection)
            return null;

        var panelToken = NewToken();
        var agentToken = NewToken();
        var expires = clock.GetUtcNow().UtcDateTime + SessionRules.TokenLifetime;
        var session = new RelaySession(opening, Hash(panelToken), Hash(agentToken), expires);
        _sessions[opening.SessionId] = session;

        try
        {
            await connection.SendAsync("abrir_sesion_remota", new
            {
                sesion_id = opening.SessionId,
                tipo = opening.Type,
                url_relay = SessionRules.RelayPath(opening.SessionId),
                token_un_uso = agentToken,
                tecnico = opening.TechnicianName,
                pedir_permiso = opening.AskConsent,
                solo_ver = opening.ViewOnly,
                archivos = opening.AllowFiles,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            _sessions.TryRemove(opening.SessionId, out _);
            return null;
        }

        // Si la otra parte no llega a tiempo, la sesión se descarta sola.
        _ = ExpireIfIncompleteAsync(session);
        return (panelToken, expires);
    }

    public async Task<bool> CloseAsync(int companyId, long sessionId, string reason, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.Opening.CompanyId != companyId)
            return false;
        await EndAsync(session, reason);
        return true;
    }

    /// <summary>Una parte se conecta: primer mensaje {"token": "..."}; luego, hasta que una cierre, todo se reenvía.</summary>
    public async Task HandleAsync(HttpContext http, long sessionId)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var token = await ReadTokenAsync(socket, http.RequestAborted);
        if (token is null || !_sessions.TryGetValue(sessionId, out var session) || !session.TryAttach(Hash(token), socket, clock.GetUtcNow().UtcDateTime, out var role, out var completed))
        {
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "Token no válido o vencido.");
            return;
        }

        logger.LogInformation("Sesión remota {SessionId}: se conectó el {Role}.", sessionId, role);
        // Solo quien completa la pareja arranca el reenvío (si llegan a la vez, uno solo lo hace).
        if (completed)
        {
            await SendTextAsync(session.Panel!, """{"relay":"conectado"}""");
            _ = PumpAsync(session);
        }

        // Cada conexión vive mientras viva la sesión (el reenvío corre en PumpAsync).
        try
        {
            await session.Ended.Task.WaitAsync(http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            await EndAsync(session, $"se cortó la conexión del {role}");
        }
    }

    private async Task PumpAsync(RelaySession session)
    {
        var a = Forward(session.Panel!, session.Agent!);
        var b = Forward(session.Agent!, session.Panel!);
        var first = await Task.WhenAny(a, b);
        await EndAsync(session, first == a ? "el técnico cerró la sesión" : "el equipo cerró la sesión");
    }

    private static async Task Forward(WebSocket from, WebSocket to)
    {
        var buffer = new byte[FrameChunk];
        try
        {
            while (from.State == WebSocketState.Open && to.State == WebSocketState.Open)
            {
                var result = await from.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // Una de las dos partes se fue.
        }
    }

    private async Task EndAsync(RelaySession session, string reason)
    {
        if (!session.MarkEnded())
            return;
        _sessions.TryRemove(session.Opening.SessionId, out _);

        if (session.Panel is { } panel)
        {
            await SendTextAsync(panel, JsonSerializer.Serialize(new { relay = "cerrada", motivo = reason }));
            await CloseQuietlyAsync(panel, WebSocketCloseStatus.NormalClosure, "Sesión terminada.");
        }
        if (session.Agent is { } agent)
            await CloseQuietlyAsync(agent, WebSocketCloseStatus.NormalClosure, "Sesión terminada.");

        // El agente suelta lo que tenga abierto (terminal, archivos, captura) aunque su socket del relay no se haya enterado.
        if (agents.FindByMachine(session.Opening.CompanyId, session.Opening.MachineId) is { } connection)
        {
            try
            {
                await connection.SendAsync("cerrar_sesion_remota", new { sesion_id = session.Opening.SessionId }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
            {
                // Desconectado: no tiene nada abierto.
            }
        }

        try
        {
            await log.EndAsync(session.Opening.CompanyId, session.Opening.SessionId, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sesión remota {SessionId}: no se pudo registrar el cierre.", session.Opening.SessionId);
        }

        logger.LogInformation("Sesión remota {SessionId} terminada: {Reason}.", session.Opening.SessionId, reason);
        session.Ended.TrySetResult();
    }

    private async Task ExpireIfIncompleteAsync(RelaySession session)
    {
        await Task.Delay(SessionRules.TokenLifetime, clock);
        if (!session.BothAttached)
            await EndAsync(session, session.Agent is null ? "el equipo no respondió" : "el técnico no llegó a conectarse");
    }

    private static async Task<string?> ReadTokenAsync(WebSocket socket, CancellationToken aborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(TokenMessageTimeout);
        var buffer = new byte[1024];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
                return null;
            using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            return json.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or WebSocketException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            // Ya cerrado.
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
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
            // Ya cerrado.
        }
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private sealed class RelaySession(RelayOpening opening, byte[] panelHash, byte[] agentHash, DateTime expiresAt)
    {
        private readonly object _gate = new();
        private bool _ended;

        public RelayOpening Opening { get; } = opening;
        public WebSocket? Panel { get; private set; }
        public WebSocket? Agent { get; private set; }
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BothAttached { get { lock (_gate) return Panel is not null && Agent is not null; } }

        /// <summary>Cada token sirve una vez, antes de vencer; la comparación es de tiempo constante.</summary>
        public bool TryAttach(byte[] tokenHash, WebSocket socket, DateTime now, out string role, out bool completed)
        {
            lock (_gate)
            {
                role = "";
                completed = false;
                if (_ended || now > expiresAt)
                    return false;
                if (Panel is null && CryptographicOperations.FixedTimeEquals(tokenHash, panelHash))
                {
                    Panel = socket;
                    role = "técnico";
                    completed = Agent is not null;
                    return true;
                }
                if (Agent is null && CryptographicOperations.FixedTimeEquals(tokenHash, agentHash))
                {
                    Agent = socket;
                    role = "equipo";
                    completed = Panel is not null;
                    return true;
                }
                return false;
            }
        }

        public bool MarkEnded()
        {
            lock (_gate)
            {
                if (_ended)
                    return false;
                _ended = true;
                return true;
            }
        }
    }
}

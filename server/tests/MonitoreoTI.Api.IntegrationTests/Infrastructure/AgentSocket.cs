using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace MonitoreoTI.Api.IntegrationTests.Infrastructure;

/// <summary>Cliente WebSocket de pruebas que habla el protocolo del agente.</summary>
public static class AgentSocket
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<WebSocket> ConnectAsync(AgentTestApi api, string? credential)
    {
        var client = api.Factory.Server.CreateWebSocketClient();
        if (credential is not null)
            client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {credential}";

        using var cts = new CancellationTokenSource(Timeout);
        return await client.ConnectAsync(new Uri("ws://localhost/agent/ws"), cts.Token);
    }

    /// <summary>Conecta y completa el saludo (hola, ack y bienvenida).</summary>
    public static async Task<WebSocket> ConnectAndGreetAsync(AgentTestApi api, RegisteredAgent agent)
    {
        var socket = await ConnectAsync(api, agent.Credential);
        await SendAsync(socket, "hola", new { version = "0.1.0", hostname = "PC", so = "Windows", es_recolector = false });
        await ReceiveAsync(socket); // ack
        await ReceiveAsync(socket); // bienvenida
        return socket;
    }

    public static async Task<string> SendAsync(WebSocket socket, string tipo, object datos, DateTimeOffset? ts = null)
    {
        var id = Guid.NewGuid().ToString();
        var json = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, tipo, id, ts = ts ?? DateTimeOffset.UtcNow, datos });
        await socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
        return id;
    }

    /// <summary>Envía un mensaje y espera su ack; falla la prueba si el servidor no lo confirma.</summary>
    public static async Task SendAckedAsync(WebSocket socket, string tipo, object datos, DateTimeOffset? ts = null)
    {
        var id = await SendAsync(socket, tipo, datos, ts);
        // El servidor también manda cosas por su cuenta (config_actualizada, sondear_ahora…) y algún ack atrasado:
        // se dejan pasar hasta que llega el de este mensaje.
        var skipped = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var reply = await ReceiveAsync(socket) ?? throw new InvalidOperationException("El servidor cerró la conexión.");
            if (reply.GetProperty("tipo").GetString() == "ack" && reply.GetProperty("datos").GetProperty("id").GetString() == id)
                return;
            skipped.Add(reply.GetProperty("tipo").GetString() ?? "?");
        }
        throw new InvalidOperationException($"No llegó el ack de {id}; llegaron: {string.Join(", ", skipped)}");
    }

    /// <summary>Siguiente mensaje del servidor; null si cerró la conexión.</summary>
    public static async Task<JsonElement?> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
                    return null;
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            return null;
        }

        return JsonDocument.Parse(message.ToArray()).RootElement.Clone();
    }
}

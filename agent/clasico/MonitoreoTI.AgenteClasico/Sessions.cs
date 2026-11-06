using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>Canal de una sesión remota por el relay: lo que el agente manda y recibe.</summary>
    public sealed class SessionChannel
    {
        private readonly WebSocketClient _socket;
        private readonly Action _close;

        public SessionChannel(int sessionId, Dictionary<string, object> request, WebSocketClient socket, Action close)
        {
            SessionId = sessionId;
            var technician = request.Str("tecnico");
            Technician = string.IsNullOrEmpty(technician) ? "Un técnico" : technician;
            AskConsent = request.Bool("pedir_permiso");
            ViewOnly = request.Bool("solo_ver");
            AllowFiles = request.Bool("archivos");
            _socket = socket;
            _close = close;
        }

        public int SessionId { get; }
        public string Technician { get; }
        public bool AskConsent { get; }
        public bool ViewOnly { get; }
        public bool AllowFiles { get; }

        public void SendText(string text) => Send(() => _socket.SendTextAsync(text));

        public void SendBinary(byte[] data) => Send(() => _socket.SendBinaryAsync(data));

        public void Close() => _close();

        private static void Send(Func<Task> send)
        {
            try
            {
                send().Wait();
            }
            catch (Exception)
            {
                // Se cerró: el cierre libera la sesión.
            }
        }
    }

    /// <summary>Lo que atiende una sesión de un tipo (terminal, archivos, escritorio); se libera al cerrar.</summary>
    public interface ISessionHandler : IDisposable
    {
        void OnText(string text);
        void OnBinary(byte[] data);
    }

    /// <summary>
    /// Sesiones remotas (decisión 84), igual que el agente normal: ante "abrir_sesion_remota" se conecta al relay del mismo
    /// servidor con su token de un solo uso y pasa los mensajes al manejador del tipo; "cerrar_sesion_remota" o el cierre
    /// del relay la terminan. Lo que se hace con archivos se informa al servidor para la auditoría.
    /// </summary>
    public sealed class RemoteSessions : IDisposable
    {
        private static readonly Regex RelayPath = new Regex(@"^/relay/\d+$");
        private readonly AgentConfig _config;
        private readonly Action<string, object> _report;
        private readonly ConcurrentDictionary<int, Active> _sessions = new ConcurrentDictionary<int, Active>();

        public RemoteSessions(AgentConfig config, Action<string, object> report)
        {
            _config = config;
            _report = report;
        }

        private sealed class Active
        {
            public WebSocketClient Socket;
            public ISessionHandler Handler;
        }

        public void Open(Dictionary<string, object> request)
        {
            var id = request.Int("sesion_id");
            var type = request.Str("tipo");
            var path = request.Str("url_relay");
            var token = request.Str("token_un_uso");
            if (id == null || type == null || path == null || token == null)
            {
                Log.Error("Pedido de sesión remota con formato inválido; se descarta.");
                return;
            }
            if (!RelayPath.IsMatch(path))
            {
                Log.Error($"Sesión {id}: relay fuera del servidor del agente; se rechaza.");
                return;
            }
            Close(id.Value);
            Task.Run(() => RunAsync(id.Value, type, path, token, request));
        }

        private async Task RunAsync(int id, string type, string path, string token, Dictionary<string, object> request)
        {
            var server = new Uri(_config.Server);
            var uri = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws", Path = path }.Uri;
            WebSocketClient socket;
            try
            {
                socket = await WebSocketClient.ConnectAsync(uri, null, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Sesión {id}: no se pudo conectar al relay: {ex.Message}");
                return;
            }
            var active = new Active { Socket = socket };
            _sessions[id] = active;
            var channel = new SessionChannel(id, request, socket, () => Close(id));
            try
            {
                await socket.SendTextAsync(Json.Write(new Dictionary<string, object> { ["token"] = token })).ConfigureAwait(false);
                try
                {
                    active.Handler = Create(type, channel);
                }
                catch (Exception ex)
                {
                    channel.SendText(Json.Write(new Dictionary<string, object> { ["tipo"] = "error", ["mensaje"] = $"No se pudo iniciar la sesión: {ex.Message}" }));
                    return;
                }
                if (active.Handler == null)
                {
                    channel.SendText(Json.Write(new Dictionary<string, object> { ["tipo"] = "error", ["mensaje"] = $"Este agente no admite sesiones de tipo {type}." }));
                    return;
                }
                Log.Info($"Sesión remota {id} abierta ({type}).");
                while (true)
                {
                    var message = await socket.ReceiveAsync().ConfigureAwait(false);
                    if (message == null)
                        break;
                    if (message.Binary)
                        active.Handler.OnBinary(message.Data);
                    else
                        active.Handler.OnText(message.Text);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"Sesión remota {id}: {ex.Message}");
            }
            finally
            {
                Release(id, active);
            }
        }

        private ISessionHandler Create(string type, SessionChannel channel)
        {
            switch (type)
            {
                case "archivos":
                    return new FileSession(channel, activity => Report(channel.SessionId, activity));
                case "terminal":
                    return new TerminalSession(channel);
                case "escritorio":
                    return new DesktopSession(channel, activity => Report(channel.SessionId, activity));
                default:
                    return null;
            }
        }

        private void Report(int sessionId, Dictionary<string, object> activity)
        {
            activity["sesionId"] = sessionId;
            _report("actividad_sesion", activity);
        }

        public void Close(int id)
        {
            if (_sessions.TryGetValue(id, out var active))
            {
                Release(id, active);
                active.Socket.Close();
            }
        }

        private void Release(int id, Active active)
        {
            if (!_sessions.TryGetValue(id, out var current) || current != active)
                return;
            _sessions.TryRemove(id, out _);
            try
            {
                active.Handler?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"Sesión {id}: falló al liberarla: {ex.Message}");
            }
            try
            {
                active.Socket.Dispose();
            }
            catch (Exception)
            {
            }
            Log.Info($"Sesión remota {id} cerrada.");
        }

        public void Dispose()
        {
            foreach (var id in _sessions.Keys)
                Close(id);
        }
    }
}

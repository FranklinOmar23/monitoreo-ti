using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Conexión con el servidor (/agent/ws), igual que el agente normal: "hola" al abrir, espera "bienvenida", latido
    /// cada heartbeat_s y reconexión con espera creciente. Lo que se reporta va a una cola en memoria y sale de a uno,
    /// esperando el "ack"; sin conexión se acumula (hasta <see cref="MaxQueue"/>, se descarta lo más viejo).
    /// </summary>
    public sealed class Gateway
    {
        public const int MaxQueue = 2000;
        private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(15);

        private readonly AgentConfig _config;
        private readonly Action<Dictionary<string, object>> _onMessage;
        private readonly Action<int> _onWelcome;
        private readonly LinkedList<Dictionary<string, object>> _queue = new LinkedList<Dictionary<string, object>>();
        private readonly object _gate = new object();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _pending = new Dictionary<string, TaskCompletionSource<bool>>();
        private readonly SemaphoreSlim _queued = new SemaphoreSlim(0);
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private WebSocketClient _socket;
        private volatile bool _ready;
        private DateTime _lastReceived;

        public Gateway(AgentConfig config, Action<int> onWelcome, Action<Dictionary<string, object>> onMessage)
        {
            _config = config;
            _onWelcome = onWelcome;
            _onMessage = onMessage;
        }

        public int HeartbeatSeconds { get; private set; } = 30;

        public void Enqueue(string tipo, object datos)
        {
            lock (_gate)
            {
                _queue.AddLast(Envelope.Create(tipo, datos));
                while (_queue.Count > MaxQueue)
                    _queue.RemoveFirst();
            }
            _queued.Release();
        }

        public async Task RunAsync(CancellationToken stop)
        {
            var attempt = 0;
            var random = new Random();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await SessionAsync(stop).ConfigureAwait(false);
                    attempt = 0;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Log.Error($"Conexión con el servidor: {ex.Message}");
                }
                _ready = false;
                FailPending();
                if (stop.IsCancellationRequested)
                    break;
                var delay = Math.Min(1000 * Math.Pow(2, attempt++), 60000) * (0.8 + random.NextDouble() * 0.4);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            _socket?.Close();
        }

        private async Task SessionAsync(CancellationToken stop)
        {
            var server = new Uri(_config.Server);
            var uri = new UriBuilder(server) { Scheme = server.Scheme == "https" ? "wss" : "ws", Path = "/agent/ws" }.Uri;
            using (var socket = await WebSocketClient.ConnectAsync(uri, _config.Credential, TimeSpan.FromSeconds(15)).ConfigureAwait(false))
            {
                _socket = socket;
                _lastReceived = DateTime.UtcNow;
                await socket.SendTextAsync(Json.Write(Envelope.Create("hola", new Dictionary<string, object>
                {
                    ["version"] = AgentInfo.Version,
                    ["hostname"] = Environment.MachineName,
                    ["so"] = Wmi.OperatingSystem().Name,
                    ["es_recolector"] = false,
                }))).ConfigureAwait(false);

                using (var session = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    var receive = ReceiveLoopAsync(socket);
                    var welcome = DateTime.UtcNow;
                    // Sin bienvenida en 10 s se reintenta.
                    while (!_ready && !receive.IsCompleted && DateTime.UtcNow - welcome < TimeSpan.FromSeconds(10))
                        await Task.Delay(100, stop).ConfigureAwait(false);
                    if (!_ready)
                        throw new InvalidOperationException("El servidor no envió la bienvenida.");
                    Log.Info("Conectado al servidor.");

                    var send = SendLoopAsync(socket, session.Token);
                    var beat = HeartbeatLoopAsync(socket, session.Token);
                    await Task.WhenAny(receive, send, beat).ConfigureAwait(false);
                    session.Cancel();
                    socket.Dispose();
                    try
                    {
                        await Task.WhenAll(send, beat).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Se cortó la conexión: ya se informa en RunAsync.
                    }
                    if (receive.IsFaulted)
                        throw receive.Exception.GetBaseException();
                }
            }
        }

        private async Task ReceiveLoopAsync(WebSocketClient socket)
        {
            while (true)
            {
                var text = await socket.ReceiveTextAsync().ConfigureAwait(false);
                if (text == null)
                    return;
                _lastReceived = DateTime.UtcNow;
                var message = Json.Read(text);
                var tipo = message.Str("tipo");
                if (tipo == null)
                    continue;
                if (tipo == "ack")
                {
                    var id = message.Obj("datos").Str("id");
                    TaskCompletionSource<bool> waiting = null;
                    lock (_gate)
                        if (id != null && _pending.TryGetValue(id, out waiting))
                            _pending.Remove(id);
                    waiting?.TrySetResult(true);
                }
                else if (tipo == "bienvenida")
                {
                    var datos = message.Obj("datos");
                    HeartbeatSeconds = Math.Max(5, datos.Int("heartbeat_s") ?? 30);
                    var key = datos.Str("llave_empresa");
                    if (!string.IsNullOrEmpty(key) && string.IsNullOrEmpty(_config.CompanyPublicKey))
                    {
                        // La llave llega una sola vez: después no se reemplaza desde la red.
                        _config.CompanyPublicKey = key;
                        _config.Save();
                        Log.Info("Llave de firma de la empresa guardada.");
                    }
                    _ready = true;
                    _onWelcome(Math.Max(30, datos.Int("metricas_s") ?? 60));
                }
                else
                {
                    try
                    {
                        _onMessage(message);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Mensaje {tipo}: {ex.Message}");
                    }
                }
            }
        }

        private async Task SendLoopAsync(WebSocketClient socket, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                Dictionary<string, object> next;
                lock (_gate)
                    next = _queue.First?.Value;
                if (next == null)
                {
                    await _queued.WaitAsync(TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                    continue;
                }
                var id = (string)next["id"];
                var ack = new TaskCompletionSource<bool>();
                lock (_gate)
                    _pending[id] = ack;
                await socket.SendTextAsync(Json.Write(next)).ConfigureAwait(false);
                var done = await Task.WhenAny(ack.Task, Task.Delay(AckTimeout, stop)).ConfigureAwait(false);
                if (done != ack.Task || !ack.Task.Result)
                    throw new TimeoutException($"El servidor no confirmó el mensaje {next["tipo"]}.");
                lock (_gate)
                    if (_queue.First?.Value == next)
                        _queue.RemoveFirst();
            }
        }

        private async Task HeartbeatLoopAsync(WebSocketClient socket, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), stop).ConfigureAwait(false);
                if (DateTime.UtcNow - _lastReceived > TimeSpan.FromSeconds(HeartbeatSeconds * 3))
                    throw new TimeoutException("El servidor dejó de responder.");
                await socket.SendTextAsync(Json.Write(Envelope.Create("heartbeat", new Dictionary<string, object>
                {
                    ["uptimeSeconds"] = (long)_uptime.Elapsed.TotalSeconds,
                }))).ConfigureAwait(false);
            }
        }

        private void FailPending()
        {
            List<TaskCompletionSource<bool>> waiting;
            lock (_gate)
            {
                waiting = _pending.Values.ToList();
                _pending.Clear();
            }
            foreach (var w in waiting)
                w.TrySetResult(false);
        }
    }
}

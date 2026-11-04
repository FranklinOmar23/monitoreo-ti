using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Mensajes entre el agente y el asistente por stdin/stdout: [tipo:1][largo:4 LE][datos]. Tipo 1 = texto JSON (UTF-8),
/// 2 = binario (pantalla). El agente los reenvía tal cual por el relay: el texto y el binario del visor.
/// </summary>
public static class FrameIo
{
    public const byte Text = 1;
    public const byte Binary = 2;
    private const int MaxFrame = 16 * 1024 * 1024;

    public static async Task<(byte Type, byte[] Data)?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        if (!await ReadExactlyAsync(stream, header, cancellationToken))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length is < 0 or > MaxFrame)
            return null;
        var data = new byte[length];
        return await ReadExactlyAsync(stream, data, cancellationToken) ? (header[0], data) : null;
    }

    public static void Write(Stream stream, object gate, byte type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], data.Length);
        lock (gate)
        {
            stream.Write(header);
            stream.Write(data);
            stream.Flush();
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Asistente de sesión ("MonitoreoTI.AgentHost.exe asistente"): corre como SYSTEM en la sesión de la consola (lo abre
/// el lanzador) y atiende el escritorio remoto: pantalla, mouse, teclado, portapapeles, Ctrl+Alt+Supr, el aviso
/// visible y el permiso del usuario. Sigue al escritorio activo: el normal, la pantalla de bloqueo o la de UAC (7.4).
/// <para>
/// Del visor: {"tipo":"inicio","tecnico","pedirPermiso"} (primero, del agente), {"tipo":"ver","video":["h264"]} (el
/// visor está listo: recién entonces se manda "listo" y la pantalla; con "video" sabe decodificar H.264 y se usa la V2), {"tipo":"ack"}, {"tipo":"raton",x,y,"accion","boton","delta"},
/// {"tipo":"tecla","codigo","abajo"}, {"tipo":"cad"}, {"tipo":"calidad","valor"}, {"tipo":"monitor","indice"},
/// {"tipo":"completo"}, {"tipo":"portapapeles","texto"}, {"tipo":"leer_portapapeles"}.
/// Al visor: {"tipo":"listo","monitores","monitor"}, {"tipo":"escritorio","nombre"} (cambió: Default, Winlogon...),
/// {"tipo":"portapapeles","texto"}, {"tipo":"fin","motivo"}, {"tipo":"error","mensaje"} y la pantalla en binario.
/// </para>
/// </summary>
public static class DesktopHelper
{
    /// <summary>Partes de pantalla en camino sin confirmar: más, y el visor se atrasa (la imagen tendría retraso).</summary>
    private const int MaxInFlight = 2;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    public static async Task<int> RunAsync()
    {
        // Píxeles físicos en cada monitor, aunque Windows esté al 125 % o 150 %.
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        var gate = new object();
        void Send(object message) => FrameIo.Write(stdout, gate, FrameIo.Text, JsonSerializer.SerializeToUtf8Bytes(message));

        using var stop = new CancellationTokenSource();
        var first = await FrameIo.ReadAsync(stdin, stop.Token);
        if (first is not { Type: FrameIo.Text } start || Parse(start.Data) is not { } hello || Str(hello, "tipo") != "inicio")
            return 2;
        var technician = Str(hello, "tecnico") ?? "Un técnico";
        var viewOnly = Bool(hello, "soloVer");

        // Nada se manda hasta que el visor escucha ("ver"): así ve cada paso (esperando permiso, transmisión).
        bool video;
        while (true)
        {
            var frame = await FrameIo.ReadAsync(stdin, stop.Token).WaitAsync(TimeSpan.FromMinutes(1));
            if (frame is null)
                return 0;
            if (frame.Value.Type == FrameIo.Text && Parse(frame.Value.Data) is { } v && Str(v, "tipo") == "ver")
            {
                video = v.TryGetProperty("video", out var codecs) && codecs.ValueKind == JsonValueKind.Array
                    && codecs.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "h264");
                break;
            }
        }

        // Si hay alguien con la sesión iniciada y en su escritorio, se le avisa (y se le pide permiso si corresponde).
        // Con el equipo bloqueado o sin nadie conectado no hay a quién preguntar: equipo desatendido.
        var user = UserName();
        var userPresent = user is not null && DesktopSwitcher.InputDesktopName() == "Default";
        if (userPresent && Bool(hello, "pedirPermiso"))
        {
            Send(new { tipo = "esperando_permiso", usuario = user, segundos = 30 });
            if (!SessionNotice.AskPermission(technician, viewOnly))
            {
                Send(new { tipo = "error", codigo = "PERMISO_DENEGADO", mensaje = $"{user} no permitió la conexión (o no respondió en 30 segundos)." });
                return 3;
            }
        }

        string? endReason = null;
        using var notice = user is not null
            ? new SessionNotice(technician, viewOnly, () =>
            {
                endReason = "El usuario del equipo terminó la sesión.";
                stop.Cancel();
            })
            : null;
        using var blackout = new Blackout();

        using var screen = new ScreenWorker(update => FrameIo.Write(stdout, gate, FrameIo.Binary, update), Send) { VideoAllowed = video };
        if (!screen.Start(user))
        {
            Send(new { tipo = "error", codigo = "SIN_PANTALLA", mensaje = "No se encontró ninguna pantalla en el equipo." });
            return 4;
        }
        screen.StartViewing();

        // Lo que manda el visor: la pantalla y el control van al hilo de la pantalla (el que está en el escritorio activo).
        while (!stop.IsCancellationRequested)
        {
            (byte Type, byte[] Data)? frame;
            try
            {
                frame = await FrameIo.ReadAsync(stdin, stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (frame is null)
                break; // el agente cerró la sesión
            if (frame.Value.Type != FrameIo.Text || Parse(frame.Value.Data) is not { } m)
                continue;
            var type = Str(m, "tipo");
            // Solo ver: nada del técnico llega al teclado, al mouse ni al sistema.
            if (viewOnly && type is "raton" or "tecla" or "cad" or "escribir" or "portapapeles" or "bloquear_entrada" or "pantalla_negra" or "accion")
                continue;
            switch (type)
            {
                case "ack":
                    screen.Acknowledge();
                    break;
                case "ping":
                    Send(new { tipo = "pong", t = m.TryGetProperty("t", out var t) ? t.GetDouble() : 0 });
                    break;
                case "raton":
                    var (x, y, action, button, delta) = (Int(m, "x"), Int(m, "y"), Str(m, "accion") ?? "mover", Int(m, "boton"), Int(m, "delta"));
                    screen.Post(area => InputInjector.Mouse(area.X + x, area.Y + y, action, button, delta));
                    break;
                case "tecla":
                    var (code, down) = (Str(m, "codigo") ?? "", Bool(m, "abajo"));
                    screen.Post(_ => InputInjector.Key(code, down));
                    break;
                case "escribir":
                    var typed = Str(m, "texto") ?? "";
                    screen.Post(_ => InputInjector.TypeText(typed));
                    break;
                case "cad":
                    screen.Post(_ =>
                    {
                        if (!SecureAttention.Send())
                            Send(new { tipo = "aviso", mensaje = "Windows no permitió enviar Ctrl+Alt+Supr en este equipo." });
                    });
                    break;
                case "calidad":
                    // 0 = automática (según la latencia de cada actualización).
                    screen.SetQuality(Int(m, "valor"));
                    break;
                case "monitor":
                    screen.SelectMonitor(Int(m, "indice"));
                    break;
                case "completo":
                    screen.RequestFull();
                    break;
                case "portapapeles":
                    var text = Str(m, "texto") ?? "";
                    screen.Post(_ => screen.SetClipboard(text));
                    break;
                case "leer_portapapeles":
                    screen.Post(_ => Send(new { tipo = "portapapeles", texto = InputInjector.GetClipboard() ?? "" }));
                    break;
                case "sincronizar":
                    screen.ClipboardSync = Bool(m, "activo");
                    break;
                case "solo_ver":
                    // Solo en un sentido: una sesión de solo ver no pasa a control.
                    viewOnly = true;
                    notice?.SetViewOnly();
                    screen.Post(_ => InputInjector.Block(false));
                    blackout.Hide();
                    Send(new { tipo = "modo", soloVer = true });
                    break;
                case "bloquear_entrada":
                    var block = Bool(m, "activo");
                    screen.Post(_ => Send(new { tipo = "entrada_bloqueada", activo = InputInjector.Block(block) && block }));
                    break;
                case "pantalla_negra":
                    var black = Bool(m, "activo") && blackout.Show();
                    if (!Bool(m, "activo"))
                        blackout.Hide();
                    Send(new
                    {
                        tipo = "pantalla_negra",
                        activo = black,
                        mensaje = Bool(m, "activo") && !black ? "Este Windows no permite ocultar la pantalla sin ocultarla también al técnico (requiere Windows 10 2004 o posterior)." : null,
                    });
                    break;
                case "accion":
                    var result = SystemActions.Run(Str(m, "accion") ?? "");
                    if (result is not null)
                        Send(new { tipo = "aviso", mensaje = result });
                    break;
            }
        }

        if (endReason is not null)
        {
            try
            {
                Send(new { tipo = "fin", motivo = endReason });
            }
            catch (IOException)
            {
                // Ya no hay a quién avisar.
            }
        }
        return 0;
    }

    /// <summary>Usuario con la sesión iniciada en esta sesión de Windows; null si nadie la inició.</summary>
    private static string? UserName()
    {
        if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, -1, 5, out var buffer, out _)) // WTS_CURRENT_SESSION, WTSUserName
            return null;
        try
        {
            var name = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        finally
        {
            Native.WTSFreeMemory(buffer);
        }
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonElement? Parse(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr memory);
    }
}

/// <summary>
/// Hilo de la pantalla: captura y control en el escritorio que esté recibiendo la entrada (Default, Winlogon o el de
/// UAC). Un hilo solo puede capturar e inyectar en el escritorio al que está unido, así que todo pasa por aquí: en cada
/// vuelta se revisa el escritorio activo y, si cambió, el hilo se une al nuevo y vuelve a crear la captura.
/// <para>
/// Calidad automática: según cuánto tarda el visor en confirmar cada actualización (si tarda, baja; si va holgado,
/// sube). Portapapeles sincronizado: lo que se copia en el equipo se manda solo al visor.
/// </para>
/// <para>
/// Pantalla V2 (7.5): si el visor decodifica H.264 y se ve un solo monitor, se usa la duplicación de DXGI con video;
/// si no se puede (o falla tres veces seguidas) sigue la V1 hasta que cambie el escritorio o el monitor.
/// </para>
/// </summary>
public sealed class ScreenWorker(Action<byte[]> sendFrame, Action<object> sendText) : IDisposable
{
    /// <summary>Monitor "Todos": el escritorio virtual completo.</summary>
    public const int AllMonitors = -1;

    private readonly System.Collections.Concurrent.BlockingCollection<Action<MonitorArea>> _queue = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<long> _sentAt = new();
    private readonly ManualResetEventSlim _viewing = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private int _inFlight;
    private volatile int _monitor;
    private volatile bool _full = true;
    private volatile bool _auto = true;
    private volatile int _quality = 60;
    private uint _clipboardSequence;
    private int _videoFailures;
    private List<MonitorArea> _monitors = [];
    private string? _user;

    /// <summary>Lo que se copia en el equipo se manda solo al visor.</summary>
    public bool ClipboardSync { get; set; }

    /// <summary>El visor decodifica H.264: se puede usar la pantalla V2.</summary>
    public bool VideoAllowed { get; init; }

    public bool Start(string? user)
    {
        _user = user;
        _monitors = ScreenCapture.Monitors();
        if (_monitors.Count == 0)
            return false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pantalla remota" };
        _thread.Start();
        return true;
    }

    /// <summary>El visor está listo: se manda "listo" y desde ahí la pantalla.</summary>
    public void StartViewing()
    {
        SendReady();
        _full = true;
        _viewing.Set();
    }

    public void Acknowledge()
    {
        Interlocked.Decrement(ref _inFlight);
        if (!_sentAt.TryDequeue(out var sent) || !_auto)
            return;
        // Calidad automática: lo que tarda una actualización en dibujarse y confirmarse.
        var elapsed = Environment.TickCount64 - sent;
        if (elapsed > 400)
            _quality = Math.Max(25, _quality - 10);
        else if (elapsed < 120)
            _quality = Math.Min(80, _quality + 2);
    }

    /// <summary>0 = automática; si no, la calidad JPEG fija.</summary>
    public void SetQuality(int value)
    {
        _auto = value <= 0;
        _quality = _auto ? 60 : Math.Clamp(value, 10, 95);
        _full = true;
    }

    public void RequestFull() => _full = true;

    public void SelectMonitor(int index)
    {
        if (index != AllMonitors && (index < 0 || index >= _monitors.Count))
            return;
        _monitor = index;
        _full = true;
        _videoFailures = 0;
    }

    /// <summary>Una acción (mouse, teclado, portapapeles) a ejecutar en el escritorio activo, con el monitor actual.</summary>
    public void Post(Action<MonitorArea> action)
    {
        _queue.Add(action);
        _wake.Set();
    }

    /// <summary>Lo que manda el visor al portapapeles del equipo (sin devolverlo como "copiado en el equipo").</summary>
    public void SetClipboard(string text)
    {
        InputInjector.SetClipboard(text);
        _clipboardSequence = InputInjector.ClipboardSequence();
    }

    private MonitorArea Area()
    {
        if (_monitor == AllMonitors && _monitors.Count > 1)
        {
            var left = _monitors.Min(m => m.X);
            var top = _monitors.Min(m => m.Y);
            return new MonitorArea(left, top, _monitors.Max(m => m.X + m.Width) - left, _monitors.Max(m => m.Y + m.Height) - top, true);
        }
        return _monitors.Count > 0 ? _monitors[Math.Clamp(_monitor, 0, _monitors.Count - 1)] : new MonitorArea(0, 0, 1, 1, true);
    }

    private void SendReady() => sendText(new
    {
        tipo = "listo",
        monitor = _monitor,
        usuario = _user,
        monitores = _monitors.Select((m, i) => new { indice = i, ancho = m.Width, alto = m.Height, principal = m.Primary }),
    });

    /// <summary>V2 si se puede; si no, V1. "Todos los monitores" va siempre por la V1 (DXGI duplica de a un monitor).</summary>
    private IScreenSource CreateSource(MonitorArea area)
    {
        if (VideoAllowed && _monitor != AllMonitors && _videoFailures < 3)
        {
            try
            {
                return new VideoScreenCapture(area);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Sin aceleración, sesión de escritorio remoto o sin codificador: la V1 hasta el próximo cambio.
                _videoFailures = 3;
            }
        }
        return new ScreenCapture(area);
    }

    private void Run()
    {
        IScreenSource? capture = null;
        var desktop = "";
        MonitorArea? current = null;
        var nextClipboardCheck = 0L;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // ¿Cambió el escritorio que recibe la entrada (bloqueo, UAC, inicio de sesión)?
                var active = DesktopSwitcher.InputDesktopName();
                if (active is not null && active != desktop && DesktopSwitcher.SwitchThreadTo(active))
                {
                    desktop = active;
                    capture?.Dispose();
                    capture = null;
                    _videoFailures = 0;
                    _monitors = ScreenCapture.Monitors();
                    if (_monitor >= _monitors.Count)
                        _monitor = 0;
                    _full = true;
                    if (_viewing.IsSet)
                        sendText(new { tipo = "escritorio", nombre = active });
                }

                var area = Area();
                if (capture is null || current != area)
                {
                    capture?.Dispose();
                    capture = CreateSource(area);
                    current = area;
                    _full = true;
                }

                // Primero el control (que no espere a la pantalla).
                while (_queue.TryTake(out var action))
                {
                    try
                    {
                        action(area);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Una entrada que falla (escritorio cambiando) no corta la sesión.
                    }
                }

                if (_viewing.IsSet && Volatile.Read(ref _inFlight) < 2)
                {
                    if (_full)
                    {
                        capture.RequestFull();
                        _full = false;
                    }
                    byte[]? update;
                    try
                    {
                        update = capture.Capture(_quality);
                        if (capture is VideoScreenCapture)
                            _videoFailures = 0;
                    }
                    catch (Exception ex) when (capture is VideoScreenCapture && ex is not OutOfMemoryException)
                    {
                        // Se perdió la duplicación (cambio de resolución, pantalla completa, UAC): se vuelve a crear, y
                        // después de tres fallas seguidas se sigue con la V1.
                        _videoFailures++;
                        capture.Dispose();
                        capture = null;
                        continue;
                    }
                    if (update is not null)
                    {
                        Interlocked.Increment(ref _inFlight);
                        _sentAt.Enqueue(Environment.TickCount64);
                        sendFrame(update);
                    }
                }

                // Portapapeles sincronizado: una vez por segundo se mira si cambió en el equipo.
                if (ClipboardSync && Environment.TickCount64 >= nextClipboardCheck)
                {
                    nextClipboardCheck = Environment.TickCount64 + 1000;
                    var sequence = InputInjector.ClipboardSequence();
                    if (sequence != _clipboardSequence)
                    {
                        _clipboardSequence = sequence;
                        var text = InputInjector.GetClipboard();
                        if (!string.IsNullOrEmpty(text))
                            sendText(new { tipo = "portapapeles", texto = text.Length > 100_000 ? text[..100_000] : text, automatico = true });
                    }
                }

                // Espera hasta la próxima vuelta (la V2 va a 30 cuadros por segundo), pero despierta en cuanto llega una
                // entrada (sin tocar su orden).
                WaitHandle.WaitAny([_wake, _stop.Token.WaitHandle], capture is VideoScreenCapture ? 1000 / VideoScreenCapture.FramesPerSecond : 50);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Fin de la sesión o el agente se fue.
        }
        finally
        {
            capture?.Dispose();
            // Si el técnico bloqueó la entrada del usuario, se le devuelve al terminar.
            InputInjector.Block(false);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
        _viewing.Dispose();
        _wake.Dispose();
    }
}

/// <summary>
/// Acciones del sistema desde el menú "Más" del visor: cambiar de usuario (se desconecta la sesión y queda la pantalla
/// de inicio), cerrar la sesión de Windows y reiniciar el equipo. Devuelve un aviso si no se pudo.
/// </summary>
public static class SystemActions
{
    public static string? Run(string action)
    {
        switch (action)
        {
            case "cambiar_usuario":
                return Native.WTSDisconnectSession(IntPtr.Zero, -1, false) ? null : "Windows no permitió cambiar de usuario.";
            case "cerrar_sesion":
                return Native.WTSLogoffSession(IntPtr.Zero, -1, false) ? null : "Windows no permitió cerrar la sesión.";
            case "reiniciar":
                var shutdown = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
                var start = new System.Diagnostics.ProcessStartInfo(shutdown) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "/r", "/t", "15", "/c", "Soporte de TI reiniciará este equipo en 15 segundos.", "/d", "p:4:1" })
                    start.ArgumentList.Add(arg);
                using (var process = System.Diagnostics.Process.Start(start))
                    process?.WaitForExit(5000);
                return null;
            default:
                return "Acción desconocida.";
        }
    }

    private static class Native
    {
        [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSDisconnectSession(IntPtr server, int session, bool wait);
        [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSLogoffSession(IntPtr server, int session, bool wait);
    }
}

/// <summary>Escritorios de Windows: cuál recibe la entrada y unir el hilo actual a él (pantalla de bloqueo, UAC).</summary>
public static class DesktopSwitcher
{
    /// <summary>Nombre del escritorio que recibe la entrada ("Default", "Winlogon"...); null si no se puede abrir.</summary>
    public static string? InputDesktopName()
    {
        var desktop = Native.OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero)
            return null;
        try
        {
            return Name(desktop);
        }
        finally
        {
            Native.CloseDesktop(desktop);
        }
    }

    /// <summary>Une el hilo actual al escritorio indicado (el hilo no debe tener ventanas propias).</summary>
    public static bool SwitchThreadTo(string name)
    {
        // Si ya está en ese escritorio no se toca: volver a unirlo al mismo deja sin imagen la captura de GDI.
        if (Name(Native.GetThreadDesktop(Native.GetCurrentThreadId())) == name)
            return true;
        var desktop = Native.OpenDesktopW(name, 0, false, 0x10000000); // GENERIC_ALL
        if (desktop == IntPtr.Zero)
            return false;
        if (Native.SetThreadDesktop(desktop))
            return true; // el escritorio queda abierto mientras el hilo lo use
        Native.CloseDesktop(desktop);
        return false;
    }

    private static string? Name(IntPtr desktop)
    {
        var buffer = new char[256];
        return Native.GetUserObjectInformationW(desktop, 2, buffer, buffer.Length * 2, out var needed) // UOI_NAME
            ? new string(buffer, 0, Math.Max(0, needed / 2 - 1))
            : null;
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] info, int length, out int needed);
    }
}

/// <summary>
/// Ctrl+Alt+Supr (secuencia de atención segura): no se puede simular con SendInput; se pide a Windows con SendSAS, que
/// exige la política SoftwareSASGeneration (la deja el instalador; si falta, se pone aquí, como SYSTEM).
/// </summary>
public static class SecureAttention
{
    private const string PolicyKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public static void EnsurePolicy()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PolicyKey);
            if (key.GetValue("SoftwareSASGeneration") is not int value || (value & 1) == 0)
                key.SetValue("SoftwareSASGeneration", 1, Microsoft.Win32.RegistryValueKind.DWord); // 1 = servicios
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Sin permiso: SendSAS fallará y se avisa al técnico.
        }
    }

    public static bool Send()
    {
        EnsurePolicy();
        try
        {
            Native.SendSAS(false);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static class Native
    {
        [DllImport("sas.dll")] public static extern void SendSAS(bool asUser);
    }
}

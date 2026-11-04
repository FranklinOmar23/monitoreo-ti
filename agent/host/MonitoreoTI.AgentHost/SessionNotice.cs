using System.Runtime.InteropServices;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Aviso en el equipo mientras haya una sesión remota (sección 10.1, decisión 52): una tarjeta siempre visible abajo a
/// la derecha ("Soporte de TI está conectado", quién y si puede controlar) con el botón "Terminar". Ventana Win32
/// propia, sin librerías de interfaz.
/// </summary>
public sealed class SessionNotice : IDisposable
{
    private const int ButtonId = 1;
    private readonly string _technician;
    private readonly Action _onTerminate;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private volatile bool _viewOnly;
    private IntPtr _window;
    private Native.WndProc? _procedure; // referencia viva: si el GC la recoge, la ventana falla

    public SessionNotice(string technician, bool viewOnly, Action onTerminate)
    {
        _technician = technician;
        _viewOnly = viewOnly;
        _onTerminate = onTerminate;
        _thread = new Thread(Run) { IsBackground = true, Name = "Aviso de sesión" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>El técnico pasó a solo ver: el aviso lo dice.</summary>
    public void SetViewOnly()
    {
        _viewOnly = true;
        if (_window != IntPtr.Zero)
            Native.InvalidateRect(_window, IntPtr.Zero, true);
    }

    private string Body => _viewOnly
        ? $"{_technician} puede ver la pantalla de este equipo."
        : $"{_technician} puede ver y controlar este equipo.";

    /// <summary>
    /// Pregunta al usuario si permite la conexión. Sin respuesta en 30 segundos, se niega: nadie se conecta a un equipo
    /// sin permiso cuando se pidió preguntar.
    /// </summary>
    public static bool AskPermission(string technician, bool viewOnly)
    {
        var what = viewOnly ? "ver su pantalla" : "ver y controlar este equipo";
        var answer = Native.MessageBoxTimeoutW(IntPtr.Zero,
            $"{technician} (Soporte de TI) quiere conectarse para {what}.\n\n¿Lo permite?\n\nSi no responde en 30 segundos, no se conecta.",
            "MonitoreoTI: solicitud de conexión remota",
            0x00000004 | 0x00000020 | 0x00040000 | 0x00010000, // YESNO | ICONQUESTION | TOPMOST | SETFOREGROUND
            0, 30_000);
        return answer == 6; // IDYES
    }

    private void Run()
    {
        var instance = Native.GetModuleHandleW(null);
        _procedure = Procedure;
        var windowClass = new Native.WndClassEx
        {
            Size = Marshal.SizeOf<Native.WndClassEx>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = instance,
            Cursor = Native.LoadCursorW(IntPtr.Zero, 32512),
            ClassName = "MonitoreoTISessionNotice",
        };
        Native.RegisterClassExW(ref windowClass);

        var scale = Native.GetDpiForSystem() / 96.0;
        var width = (int)(360 * scale);
        var height = (int)(96 * scale);
        // Abajo a la derecha, sobre la barra de tareas (área de trabajo del monitor principal).
        var work = new Native.Rect();
        Native.SystemParametersInfoW(0x0030, 0, ref work, 0); // SPI_GETWORKAREA
        var margin = (int)(16 * scale);
        _window = Native.CreateWindowExW(
            0x00000008 | 0x00000080 | 0x08000000, // TOPMOST | TOOLWINDOW | NOACTIVATE
            windowClass.ClassName, "Soporte de TI conectado", 0x80000000 | 0x10000000 | 0x00800000, // POPUP | VISIBLE | BORDER
            work.Right - width - margin, work.Bottom - height - margin, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        var buttonWidth = (int)(90 * scale);
        var buttonHeight = (int)(28 * scale);
        Native.CreateWindowExW(0, "BUTTON", "Terminar", 0x40000000 | 0x10000000, // CHILD | VISIBLE
            width - buttonWidth - (int)(12 * scale), height - buttonHeight - (int)(10 * scale), buttonWidth, buttonHeight,
            _window, (IntPtr)ButtonId, instance, IntPtr.Zero);
        _ready.Set();

        while (Native.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            Native.TranslateMessage(ref message);
            Native.DispatchMessageW(ref message);
        }
    }

    private IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case 0x000F: // WM_PAINT
                var dc = Native.BeginPaint(window, out var paint);
                Native.GetClientRect(window, out var rect);
                var scale = Native.GetDpiForSystem() / 96.0;
                var background = Native.CreateSolidBrush(0x00FFFFFF);
                Native.FillRect(dc, ref rect, background);
                Native.DeleteObject(background);
                // Franja de color a la izquierda (verde azulado #0f766e).
                var stripe = new Native.Rect { Left = 0, Top = 0, Right = (int)(4 * scale), Bottom = rect.Bottom };
                var accent = Native.CreateSolidBrush(0x006E760F);
                Native.FillRect(dc, ref stripe, accent);
                Native.DeleteObject(accent);
                Native.SetBkMode(dc, 1);

                var title = new Native.Rect { Left = (int)(16 * scale), Top = (int)(10 * scale), Right = rect.Right - (int)(12 * scale), Bottom = (int)(30 * scale) };
                Native.SetTextColor(dc, 0x00201B0D);
                Native.DrawTextW(dc, "Soporte de TI está conectado", -1, ref title, 0x0020 | 0x8000); // SINGLELINE | END_ELLIPSIS
                var body = new Native.Rect { Left = (int)(16 * scale), Top = (int)(32 * scale), Right = rect.Right - (int)(12 * scale), Bottom = rect.Bottom - (int)(40 * scale) };
                Native.SetTextColor(dc, 0x0079746A);
                Native.DrawTextW(dc, Body, -1, ref body, 0x0010 | 0x8000); // WORDBREAK | END_ELLIPSIS
                Native.EndPaint(window, ref paint);
                return IntPtr.Zero;
            case 0x0111 when (wParam.ToInt64() & 0xFFFF) == ButtonId: // WM_COMMAND del botón
                _onTerminate();
                return IntPtr.Zero;
            case 0x0010: // WM_CLOSE
                Native.DestroyWindow(window);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
            Native.PostMessageW(_window, 0x0010, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    internal static class Native
    {
        public delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public int Size, Style;
            public IntPtr Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr IconSmall;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PaintStruct
        {
            public IntPtr Dc;
            public bool Erase;
            public Rect Paint;
            public bool Restore, IncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Msg
        {
            public IntPtr Window;
            public uint Message;
            public IntPtr WParam, LParam;
            public uint Time;
            public int X, Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WndClassEx windowClass);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(int exStyle, string className, string title, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetMessageW(out Msg message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Msg message);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref Msg message);
        [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
        [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr window, out PaintStruct paint);
        [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr window, ref PaintStruct paint);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(IntPtr dc, string text, int count, ref Rect rect, uint format);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr instance, int name);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint action, uint param, ref Rect value, uint winIni);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBoxTimeoutW(IntPtr window, string text, string caption, uint type, ushort language, int milliseconds);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] public static extern uint SetTextColor(IntPtr dc, uint color);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? name);
    }
}

/// <summary>
/// Pantalla en negro para el usuario: una ventana negra encima de todo, en todos los monitores, que la captura no ve
/// (SetWindowDisplayAffinity con WDA_EXCLUDEFROMCAPTURE, Windows 10 2004 o posterior). Si Windows no permite excluirla
/// de la captura, no se muestra: el técnico también vería negro.
/// </summary>
public sealed class Blackout : IDisposable
{
    private const uint ExcludeFromCapture = 0x00000011;
    private Thread? _thread;
    private IntPtr _window;
    private SessionNotice.Native.WndProc? _procedure;

    public bool Show()
    {
        if (_window != IntPtr.Zero)
            return true;
        var ready = new ManualResetEventSlim();
        var ok = false;
        _thread = new Thread(() =>
        {
            var instance = SessionNotice.Native.GetModuleHandleW(null);
            _procedure = Procedure;
            var windowClass = new SessionNotice.Native.WndClassEx
            {
                Size = Marshal.SizeOf<SessionNotice.Native.WndClassEx>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = instance,
                ClassName = "MonitoreoTIBlackout",
            };
            SessionNotice.Native.RegisterClassExW(ref windowClass);
            // Todo el escritorio virtual (todos los monitores).
            int x = SessionNotice.Native.GetSystemMetrics(76), y = SessionNotice.Native.GetSystemMetrics(77);
            int w = SessionNotice.Native.GetSystemMetrics(78), h = SessionNotice.Native.GetSystemMetrics(79);
            _window = SessionNotice.Native.CreateWindowExW(0x00000008 | 0x00000080, windowClass.ClassName, "Soporte de TI trabajando",
                0x80000000, x, y, w, h, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero); // TOPMOST | TOOLWINDOW, POPUP
            ok = _window != IntPtr.Zero && SessionNotice.Native.SetWindowDisplayAffinity(_window, ExcludeFromCapture);
            if (ok)
                SessionNotice.Native.ShowWindow(_window, 4); // SW_SHOWNOACTIVATE
            else if (_window != IntPtr.Zero)
            {
                SessionNotice.Native.DestroyWindow(_window);
                _window = IntPtr.Zero;
            }
            ready.Set();
            if (!ok)
                return;
            while (SessionNotice.Native.GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                SessionNotice.Native.TranslateMessage(ref message);
                SessionNotice.Native.DispatchMessageW(ref message);
            }
        }) { IsBackground = true, Name = "Pantalla en negro" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        return ok;
    }

    public void Hide()
    {
        if (_window == IntPtr.Zero)
            return;
        SessionNotice.Native.PostMessageW(_window, 0x0010, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(2));
        _window = IntPtr.Zero;
    }

    private IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case 0x000F: // WM_PAINT: negro con un mensaje para el usuario
                var dc = SessionNotice.Native.BeginPaint(window, out var paint);
                SessionNotice.Native.GetClientRect(window, out var rect);
                var brush = SessionNotice.Native.CreateSolidBrush(0x00000000);
                SessionNotice.Native.FillRect(dc, ref rect, brush);
                SessionNotice.Native.DeleteObject(brush);
                SessionNotice.Native.SetBkMode(dc, 1);
                SessionNotice.Native.SetTextColor(dc, 0x00B0B0B0);
                SessionNotice.Native.DrawTextW(dc, "Soporte de TI está trabajando en este equipo. Por favor, espere.", -1, ref rect,
                    0x0001 | 0x0004 | 0x0020); // CENTER | VCENTER | SINGLELINE
                SessionNotice.Native.EndPaint(window, ref paint);
                return IntPtr.Zero;
            case 0x0010:
                SessionNotice.Native.DestroyWindow(window);
                return IntPtr.Zero;
            case 0x0002:
                SessionNotice.Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return SessionNotice.Native.DefWindowProcW(window, message, wParam, lParam);
    }

    public void Dispose() => Hide();
}

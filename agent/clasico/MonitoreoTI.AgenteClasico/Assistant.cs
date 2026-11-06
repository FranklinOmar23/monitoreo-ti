using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Asistente de sesión ("MonitoreoTI.AgenteClasico.exe asistente", decisión 84): corre como SYSTEM en la sesión de la
    /// consola y atiende el escritorio remoto con el mismo protocolo que el agente normal (pantalla V1: GDI + JPEG por
    /// mosaicos, que el visor del panel ya entiende). Mouse, teclado, portapapeles, Ctrl+Alt+Supr, el aviso visible y el
    /// permiso del usuario; sigue al escritorio activo (normal, bloqueo o UAC).
    /// </summary>
    public static class Assistant
    {
        public static int Run()
        {
            Native.SetProcessDPIAware();
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var gate = new object();
            Action<object> send = message => FrameIo.WriteText(stdout, gate, message);

            var first = FrameIo.Read(stdin);
            var hello = first != null && first.Type == FrameIo.Text ? Json.Read(Encoding.UTF8.GetString(first.Data)) : null;
            if (hello?.Str("tipo") != "inicio")
                return 2;
            var technician = hello.Str("tecnico") ?? "Un técnico";
            var viewOnly = hello.Bool("soloVer");

            // Nada se manda hasta que el visor escucha ("ver").
            while (true)
            {
                var frame = FrameIo.Read(stdin);
                if (frame == null)
                    return 0;
                if (frame.Type == FrameIo.Text && Json.Read(Encoding.UTF8.GetString(frame.Data))?.Str("tipo") == "ver")
                    break;
            }

            var user = UserName();
            var userPresent = user != null && DesktopSwitcher.InputDesktopName() == "Default";
            if (userPresent && hello.Bool("pedirPermiso"))
            {
                send(new Dictionary<string, object> { ["tipo"] = "esperando_permiso", ["usuario"] = user, ["segundos"] = 30 });
                if (!SessionNotice.AskPermission(technician, viewOnly))
                {
                    send(new Dictionary<string, object> { ["tipo"] = "error", ["codigo"] = "PERMISO_DENEGADO", ["mensaje"] = $"{user} no permitió la conexión (o no respondió en 30 segundos)." });
                    return 3;
                }
            }

            string endReason = null;
            var stop = new ManualResetEvent(false);
            using (var notice = user != null ? new SessionNotice(technician, viewOnly, () => { endReason = "El usuario del equipo terminó la sesión."; stop.Set(); stdin.Dispose(); }) : null)
            using (var screen = new ScreenWorker(update => FrameIo.Write(stdout, gate, FrameIo.Binary, update), send))
            {
                if (!screen.Start(user))
                {
                    send(new Dictionary<string, object> { ["tipo"] = "error", ["codigo"] = "SIN_PANTALLA", ["mensaje"] = "No se encontró ninguna pantalla en el equipo." });
                    return 4;
                }
                screen.StartViewing();

                while (!stop.WaitOne(0))
                {
                    FrameIo.Frame frame;
                    try
                    {
                        frame = FrameIo.Read(stdin);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                    if (frame == null)
                        break; // el agente cerró la sesión
                    if (frame.Type != FrameIo.Text)
                        continue;
                    var m = Json.Read(Encoding.UTF8.GetString(frame.Data));
                    var type = m?.Str("tipo");
                    if (type == null)
                        continue;
                    // Solo ver: nada del técnico llega al teclado, al mouse ni al sistema.
                    if (viewOnly && new[] { "raton", "tecla", "cad", "escribir", "portapapeles", "bloquear_entrada", "pantalla_negra", "accion" }.Contains(type))
                        continue;
                    switch (type)
                    {
                        case "ack":
                            screen.Acknowledge();
                            break;
                        case "ping":
                            send(new Dictionary<string, object> { ["tipo"] = "pong", ["t"] = m.Double("t") ?? 0 });
                            break;
                        case "raton":
                        {
                            int x = I(m, "x"), y = I(m, "y"), button = I(m, "boton"), delta = I(m, "delta");
                            var action = m.Str("accion") ?? "mover";
                            screen.Post(area => Input.Mouse(area.X + x, area.Y + y, action, button, delta));
                            break;
                        }
                        case "tecla":
                        {
                            var code = m.Str("codigo") ?? "";
                            var down = m.Bool("abajo");
                            screen.Post(_ => Input.Key(code, down));
                            break;
                        }
                        case "escribir":
                        {
                            var typed = m.Str("texto") ?? "";
                            screen.Post(_ => Input.TypeText(typed));
                            break;
                        }
                        case "cad":
                            screen.Post(_ =>
                            {
                                if (!SecureAttention.Send())
                                    send(new Dictionary<string, object> { ["tipo"] = "aviso", ["mensaje"] = "Windows no permitió enviar Ctrl+Alt+Supr en este equipo." });
                            });
                            break;
                        case "calidad":
                            screen.SetQuality(I(m, "valor"));
                            break;
                        case "monitor":
                            screen.SelectMonitor(I(m, "indice"));
                            break;
                        case "completo":
                            screen.RequestFull();
                            break;
                        case "portapapeles":
                        {
                            var text = m.Str("texto") ?? "";
                            screen.Post(_ => screen.SetClipboard(text));
                            break;
                        }
                        case "leer_portapapeles":
                            screen.Post(_ => send(new Dictionary<string, object> { ["tipo"] = "portapapeles", ["texto"] = Input.GetClipboard() ?? "" }));
                            break;
                        case "sincronizar":
                            screen.ClipboardSync = m.Bool("activo");
                            break;
                        case "solo_ver":
                            viewOnly = true;
                            notice?.SetViewOnly();
                            screen.Post(_ => Input.Block(false));
                            send(new Dictionary<string, object> { ["tipo"] = "modo", ["soloVer"] = true });
                            break;
                        case "bloquear_entrada":
                        {
                            var block = m.Bool("activo");
                            screen.Post(_ => send(new Dictionary<string, object> { ["tipo"] = "entrada_bloqueada", ["activo"] = Input.Block(block) && block }));
                            break;
                        }
                        case "pantalla_negra":
                            // Ocultar la pantalla al usuario sin ocultarla al técnico requiere Windows 10 2004 o posterior.
                            send(new Dictionary<string, object>
                            {
                                ["tipo"] = "pantalla_negra",
                                ["activo"] = false,
                                ["mensaje"] = m.Bool("activo") ? "Este Windows no permite ocultar la pantalla sin ocultarla también al técnico (requiere Windows 10 2004 o posterior)." : null,
                            });
                            break;
                        case "accion":
                        {
                            var result = SystemActions.Run(m.Str("accion") ?? "");
                            if (result != null)
                                send(new Dictionary<string, object> { ["tipo"] = "aviso", ["mensaje"] = result });
                            break;
                        }
                    }
                }
            }

            if (endReason != null)
            {
                try
                {
                    send(new Dictionary<string, object> { ["tipo"] = "fin", ["motivo"] = endReason });
                }
                catch (IOException)
                {
                }
            }
            return 0;
        }

        private static int I(Dictionary<string, object> m, string key) => (int)Math.Round(m.Double(key) ?? 0);

        private static string UserName()
        {
            if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, -1, 5, out var buffer, out _))
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

        private static class Native
        {
            [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
            [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
            public static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
            [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr memory);
        }
    }

    /// <summary>Un monitor en coordenadas del escritorio virtual.</summary>
    public sealed class MonitorArea
    {
        public MonitorArea(int x, int y, int width, int height, bool primary)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Primary = primary;
        }

        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }
        public bool Primary { get; }

        public bool SameAs(MonitorArea other) =>
            other != null && other.X == X && other.Y == Y && other.Width == Width && other.Height == Height;
    }

    /// <summary>
    /// Hilo de la pantalla: captura y control en el escritorio que recibe la entrada. Calidad automática según lo que tarda
    /// el visor en confirmar cada actualización; portapapeles sincronizado si el visor lo pide.
    /// </summary>
    public sealed class ScreenWorker : IDisposable
    {
        public const int AllMonitors = -1;
        private readonly Action<byte[]> _sendFrame;
        private readonly Action<object> _sendText;
        private readonly BlockingCollection<Action<MonitorArea>> _queue = new BlockingCollection<Action<MonitorArea>>();
        private readonly ConcurrentQueue<long> _sentAt = new ConcurrentQueue<long>();
        private readonly ManualResetEventSlim _viewing = new ManualResetEventSlim();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread _thread;
        private int _inFlight;
        private volatile int _monitor;
        private volatile bool _full = true;
        private volatile bool _auto = true;
        private volatile int _quality = 60;
        private uint _clipboardSequence;
        private List<MonitorArea> _monitors = new List<MonitorArea>();
        private string _user;

        public ScreenWorker(Action<byte[]> sendFrame, Action<object> sendText)
        {
            _sendFrame = sendFrame;
            _sendText = sendText;
        }

        public bool ClipboardSync { get; set; }

        public bool Start(string user)
        {
            _user = user;
            _monitors = ScreenCapture.Monitors();
            if (_monitors.Count == 0)
                return false;
            _thread = new Thread(Run) { IsBackground = true, Name = "Pantalla remota" };
            _thread.Start();
            return true;
        }

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
            var elapsed = _clock.ElapsedMilliseconds - sent;
            if (elapsed > 400)
                _quality = Math.Max(25, _quality - 10);
            else if (elapsed < 120)
                _quality = Math.Min(80, _quality + 2);
        }

        public void SetQuality(int value)
        {
            _auto = value <= 0;
            _quality = _auto ? 60 : Math.Max(10, Math.Min(95, value));
            _full = true;
        }

        public void RequestFull() => _full = true;

        public void SelectMonitor(int index)
        {
            if (index != AllMonitors && (index < 0 || index >= _monitors.Count))
                return;
            _monitor = index;
            _full = true;
        }

        public void Post(Action<MonitorArea> action)
        {
            _queue.Add(action);
            _wake.Set();
        }

        public void SetClipboard(string text)
        {
            Input.SetClipboard(text);
            _clipboardSequence = Input.ClipboardSequence();
        }

        private MonitorArea Area()
        {
            if (_monitor == AllMonitors && _monitors.Count > 1)
            {
                var left = _monitors.Min(m => m.X);
                var top = _monitors.Min(m => m.Y);
                return new MonitorArea(left, top, _monitors.Max(m => m.X + m.Width) - left, _monitors.Max(m => m.Y + m.Height) - top, true);
            }
            return _monitors.Count > 0 ? _monitors[Math.Max(0, Math.Min(_monitor, _monitors.Count - 1))] : new MonitorArea(0, 0, 1, 1, true);
        }

        private void SendReady() => _sendText(new Dictionary<string, object>
        {
            ["tipo"] = "listo",
            ["monitor"] = _monitor,
            ["usuario"] = _user,
            ["monitores"] = _monitors.Select((m, i) => (object)new Dictionary<string, object> { ["indice"] = i, ["ancho"] = m.Width, ["alto"] = m.Height, ["principal"] = m.Primary }).ToList(),
        });

        private void Run()
        {
            ScreenCapture capture = null;
            var desktop = "";
            MonitorArea current = null;
            var nextClipboardCheck = 0L;
            try
            {
                while (!_stop.WaitOne(0))
                {
                    var active = DesktopSwitcher.InputDesktopName();
                    if (active != null && active != desktop && DesktopSwitcher.SwitchThreadTo(active))
                    {
                        desktop = active;
                        capture?.Dispose();
                        capture = null;
                        _monitors = ScreenCapture.Monitors();
                        if (_monitor >= _monitors.Count)
                            _monitor = 0;
                        _full = true;
                        if (_viewing.IsSet)
                            _sendText(new Dictionary<string, object> { ["tipo"] = "escritorio", ["nombre"] = active });
                    }

                    var area = Area();
                    if (capture == null || !area.SameAs(current))
                    {
                        capture?.Dispose();
                        capture = new ScreenCapture(area);
                        current = area;
                        _full = true;
                    }

                    while (_queue.TryTake(out var action))
                    {
                        try
                        {
                            action(area);
                        }
                        catch (Exception)
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
                        var update = capture.Capture(_quality);
                        if (update != null)
                        {
                            Interlocked.Increment(ref _inFlight);
                            _sentAt.Enqueue(_clock.ElapsedMilliseconds);
                            _sendFrame(update);
                        }
                    }

                    if (ClipboardSync && _clock.ElapsedMilliseconds >= nextClipboardCheck)
                    {
                        nextClipboardCheck = _clock.ElapsedMilliseconds + 1000;
                        var sequence = Input.ClipboardSequence();
                        if (sequence != _clipboardSequence)
                        {
                            _clipboardSequence = sequence;
                            var text = Input.GetClipboard();
                            if (!string.IsNullOrEmpty(text))
                                _sendText(new Dictionary<string, object> { ["tipo"] = "portapapeles", ["texto"] = text.Length > 100000 ? text.Substring(0, 100000) : text, ["automatico"] = true });
                        }
                    }

                    WaitHandle.WaitAny(new WaitHandle[] { _wake, _stop }, 50);
                }
            }
            catch (IOException)
            {
                // El agente se fue.
            }
            finally
            {
                capture?.Dispose();
                Input.Block(false);
            }
        }

        public void Dispose()
        {
            _stop.Set();
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Pantalla V1: captura con GDI (BitBlt) en un mapa de bits, dibuja el cursor, compara con la captura anterior por
    /// mosaicos de 64 píxeles y codifica en JPEG solo lo que cambió. Formato (little endian):
    /// [1][ancho:2][alto:2][rectángulos:2] y por cada uno [x:2][y:2][ancho:2][alto:2][largo:4][JPEG].
    /// </summary>
    public sealed class ScreenCapture : IDisposable
    {
        public const int Tile = 64;
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        private readonly IntPtr _screenDc;
        private readonly IntPtr _memoryDc;
        private readonly MonitorArea _area;
        private IntPtr _bitmap;
        private IntPtr _bits;
        private byte[] _previous;
        private byte[] _current;
        private bool _forceFull = true;

        public ScreenCapture(MonitorArea area)
        {
            _area = area;
            _screenDc = Native.GetDC(IntPtr.Zero);
            _memoryDc = Native.CreateCompatibleDC(_screenDc);
            var info = new Native.BitmapInfoHeader
            {
                Size = Marshal.SizeOf(typeof(Native.BitmapInfoHeader)),
                Width = area.Width,
                Height = -area.Height,
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = Native.CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero)
                throw new InvalidOperationException("No se pudo reservar la memoria de la captura.");
            Native.SelectObject(_memoryDc, _bitmap);
            _previous = new byte[Stride * area.Height];
            _current = new byte[Stride * area.Height];
        }

        private int Stride => _area.Width * 4;

        public void RequestFull() => _forceFull = true;

        public byte[] Capture(int quality)
        {
            if (!Native.BitBlt(_memoryDc, 0, 0, _area.Width, _area.Height, _screenDc, _area.X, _area.Y, 0x00CC0020 | 0x40000000))
                return null;
            DrawCursor();
            Marshal.Copy(_bits, _current, 0, _current.Length);

            var rects = Changed(_forceFull);
            _forceFull = false;
            if (rects.Count == 0)
                return null;
            Buffer.BlockCopy(_current, 0, _previous, 0, _current.Length);

            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write((byte)1);
                writer.Write((ushort)_area.Width);
                writer.Write((ushort)_area.Height);
                writer.Write((ushort)rects.Count);
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(10, Math.Min(95, quality)));
                    foreach (var r in rects)
                    {
                        byte[] jpeg;
                        using (var tile = new Bitmap(r.Width, r.Height, Stride, PixelFormat.Format32bppRgb, _bits + r.Y * Stride + r.X * 4))
                        using (var ms = new MemoryStream())
                        {
                            tile.Save(ms, JpegCodec, parameters);
                            jpeg = ms.ToArray();
                        }
                        writer.Write((ushort)r.X);
                        writer.Write((ushort)r.Y);
                        writer.Write((ushort)r.Width);
                        writer.Write((ushort)r.Height);
                        writer.Write(jpeg.Length);
                        writer.Write(jpeg);
                    }
                }
                writer.Flush();
                return output.ToArray();
            }
        }

        private List<Rectangle> Changed(bool full)
        {
            var result = new List<Rectangle>();
            var columns = (_area.Width + Tile - 1) / Tile;
            var rows = (_area.Height + Tile - 1) / Tile;
            for (var ty = 0; ty < rows; ty++)
            {
                var y = ty * Tile;
                var h = Math.Min(Tile, _area.Height - y);
                var start = -1;
                for (var tx = 0; tx <= columns; tx++)
                {
                    var changed = tx < columns && (full || TileChanged(tx * Tile, y, Math.Min(Tile, _area.Width - tx * Tile), h));
                    if (changed && start < 0)
                        start = tx;
                    else if (!changed && start >= 0)
                    {
                        var x = start * Tile;
                        result.Add(new Rectangle(x, y, Math.Min(tx * Tile, _area.Width) - x, h));
                        start = -1;
                    }
                }
            }
            return result;
        }

        private unsafe bool TileChanged(int x, int y, int w, int h)
        {
            fixed (byte* current = _current)
            fixed (byte* previous = _previous)
            {
                for (var row = y; row < y + h; row++)
                {
                    var offset = row * Stride + x * 4;
                    if (Native.memcmp(current + offset, previous + offset, (UIntPtr)(w * 4)) != 0)
                        return true;
                }
            }
            return false;
        }

        private void DrawCursor()
        {
            var info = new Native.CursorInfo { Size = Marshal.SizeOf(typeof(Native.CursorInfo)) };
            if (!Native.GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero)
                return;
            if (!Native.GetIconInfo(info.Cursor, out var icon))
                return;
            Native.DrawIconEx(_memoryDc, info.X - icon.HotspotX - _area.X, info.Y - icon.HotspotY - _area.Y, info.Cursor, 0, 0, 0, IntPtr.Zero, 3);
            if (icon.Mask != IntPtr.Zero)
                Native.DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero)
                Native.DeleteObject(icon.Color);
        }

        public static List<MonitorArea> Monitors()
        {
            var list = new List<MonitorArea>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, dc, rect, data) =>
            {
                var info = new Native.MonitorInfo { Size = Marshal.SizeOf(typeof(Native.MonitorInfo)) };
                if (Native.GetMonitorInfo(monitor, ref info))
                {
                    var r = info.Monitor;
                    list.Add(new MonitorArea(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.Flags & 1) != 0));
                }
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(m => m.Primary).ThenBy(m => m.X).ToList();
        }

        public void Dispose()
        {
            if (_bitmap != IntPtr.Zero)
                Native.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
            Native.DeleteDC(_memoryDc);
            Native.ReleaseDC(IntPtr.Zero, _screenDc);
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct BitmapInfoHeader
            {
                public int Size, Width, Height;
                public short Planes, BitCount;
                public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rect
            {
                public int Left, Top, Right, Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MonitorInfo
            {
                public int Size;
                public Rect Monitor, Work;
                public int Flags;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct CursorInfo
            {
                public int Size, Flags;
                public IntPtr Cursor;
                public int X, Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct IconInfo
            {
                public bool IsIcon;
                public int HotspotX, HotspotY;
                public IntPtr Mask, Color;
            }

            public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

            [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
            [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
            [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
            [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader info, int usage, out IntPtr bits, IntPtr section, int offset);
            [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
            [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CursorInfo info);
            [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
            [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
            [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
            [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
            [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern unsafe int memcmp(byte* a, byte* b, UIntPtr count);
        }
    }

    /// <summary>Mouse, teclado y portapapeles con SendInput (x86 y x64: la estructura INPUT se arma según la arquitectura).</summary>
    public static class Input
    {
        private static readonly uint[][] Buttons = { new uint[] { 0x0002, 0x0004 }, new uint[] { 0x0020, 0x0040 }, new uint[] { 0x0008, 0x0010 } };

        public static void Mouse(int x, int y, string action, int button, int delta)
        {
            var vx = Native.GetSystemMetrics(76);
            var vy = Native.GetSystemMetrics(77);
            var vw = Math.Max(2, Native.GetSystemMetrics(78));
            var vh = Math.Max(2, Native.GetSystemMetrics(79));
            var flags = 0x0001u | 0x8000u | 0x4000u; // MOVE | ABSOLUTE | VIRTUALDESK
            var data = 0u;
            var b = Buttons[Math.Max(0, Math.Min(2, button))];
            switch (action)
            {
                case "abajo": flags |= b[0]; break;
                case "arriba": flags |= b[1]; break;
                case "rueda": flags |= 0x0800; data = unchecked((uint)Math.Max(-1200, Math.Min(1200, delta))); break;
                case "rueda_h": flags |= 0x1000; data = unchecked((uint)Math.Max(-1200, Math.Min(1200, delta))); break;
            }
            var input = new Native.INPUT
            {
                Type = 0,
                U = new Native.InputUnion
                {
                    Mouse = new Native.MOUSEINPUT
                    {
                        Dx = (int)((long)(x - vx) * 65535 / (vw - 1)),
                        Dy = (int)((long)(y - vy) * 65535 / (vh - 1)),
                        MouseData = data,
                        Flags = flags,
                    },
                },
            };
            Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        public static bool Key(string code, bool down)
        {
            if (!ScanCodes.TryGetValue(code, out var key))
                return false;
            var flags = 0x0008u | ((key & 0x10000) != 0 ? 0x0001u : 0) | (down ? 0 : 0x0002u);
            var input = new Native.INPUT { Type = 1, U = new Native.InputUnion { Keyboard = new Native.KEYBDINPUT { Scan = (ushort)(key & 0xFFFF), Flags = flags } } };
            Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.INPUT)));
            return true;
        }

        public static void TypeText(string text)
        {
            foreach (var ch in text.Length > 10000 ? text.Substring(0, 10000) : text)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n')
                {
                    Key("Enter", true);
                    Key("Enter", false);
                    continue;
                }
                var downKey = new Native.INPUT { Type = 1, U = new Native.InputUnion { Keyboard = new Native.KEYBDINPUT { Scan = ch, Flags = 0x0004 } } };
                var upKey = new Native.INPUT { Type = 1, U = new Native.InputUnion { Keyboard = new Native.KEYBDINPUT { Scan = ch, Flags = 0x0004 | 0x0002 } } };
                Native.SendInput(2, new[] { downKey, upKey }, Marshal.SizeOf(typeof(Native.INPUT)));
            }
        }

        public static bool Block(bool block) => Native.BlockInput(block);

        public static uint ClipboardSequence() => Native.GetClipboardSequenceNumber();

        /// <summary>Teclas físicas (KeyboardEvent.code) → código de exploración; el bit 16 marca las extendidas.</summary>
        public static readonly Dictionary<string, int> ScanCodes = Build();

        private static Dictionary<string, int> Build()
        {
            const int E = 0x10000;
            var map = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Escape"] = 0x01, ["Minus"] = 0x0C, ["Equal"] = 0x0D, ["Backspace"] = 0x0E, ["Tab"] = 0x0F, ["BracketLeft"] = 0x1A,
                ["BracketRight"] = 0x1B, ["Enter"] = 0x1C, ["ControlLeft"] = 0x1D, ["Semicolon"] = 0x27, ["Quote"] = 0x28, ["Backquote"] = 0x29,
                ["ShiftLeft"] = 0x2A, ["Backslash"] = 0x2B, ["Comma"] = 0x33, ["Period"] = 0x34, ["Slash"] = 0x35, ["ShiftRight"] = 0x36,
                ["NumpadMultiply"] = 0x37, ["AltLeft"] = 0x38, ["Space"] = 0x39, ["CapsLock"] = 0x3A, ["NumLock"] = 0x45 | E, ["ScrollLock"] = 0x46,
                ["Numpad7"] = 0x47, ["Numpad8"] = 0x48, ["Numpad9"] = 0x49, ["NumpadSubtract"] = 0x4A, ["Numpad4"] = 0x4B, ["Numpad5"] = 0x4C,
                ["Numpad6"] = 0x4D, ["NumpadAdd"] = 0x4E, ["Numpad1"] = 0x4F, ["Numpad2"] = 0x50, ["Numpad3"] = 0x51, ["Numpad0"] = 0x52,
                ["NumpadDecimal"] = 0x53, ["IntlBackslash"] = 0x56, ["F11"] = 0x57, ["F12"] = 0x58, ["IntlRo"] = 0x73, ["NumpadEnter"] = 0x1C | E,
                ["ControlRight"] = 0x1D | E, ["NumpadDivide"] = 0x35 | E, ["PrintScreen"] = 0x37 | E, ["AltRight"] = 0x38 | E, ["Home"] = 0x47 | E,
                ["ArrowUp"] = 0x48 | E, ["PageUp"] = 0x49 | E, ["ArrowLeft"] = 0x4B | E, ["ArrowRight"] = 0x4D | E, ["End"] = 0x4F | E,
                ["ArrowDown"] = 0x50 | E, ["PageDown"] = 0x51 | E, ["Insert"] = 0x52 | E, ["Delete"] = 0x53 | E, ["MetaLeft"] = 0x5B | E,
                ["MetaRight"] = 0x5C | E, ["ContextMenu"] = 0x5D | E,
            };
            for (var i = 1; i <= 9; i++)
                map["Digit" + i] = 0x01 + i;
            map["Digit0"] = 0x0B;
            for (var i = 1; i <= 10; i++)
                map["F" + i] = 0x3A + i;
            foreach (var row in new[] { new KeyValuePair<string, int>("QWERTYUIOP", 0x10), new KeyValuePair<string, int>("ASDFGHJKL", 0x1E), new KeyValuePair<string, int>("ZXCVBNM", 0x2C) })
            {
                for (var i = 0; i < row.Key.Length; i++)
                    map["Key" + row.Key[i]] = row.Value + i;
            }
            return map;
        }

        public static void SetClipboard(string text)
        {
            if (!OpenClipboardRetry())
                return;
            try
            {
                Native.EmptyClipboard();
                var memory = Native.GlobalAlloc(0x0002, (UIntPtr)((text.Length + 1) * 2));
                var target = Native.GlobalLock(memory);
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
                Native.GlobalUnlock(memory);
                Native.SetClipboardData(13, memory);
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        public static string GetClipboard()
        {
            if (!OpenClipboardRetry())
                return null;
            try
            {
                var handle = Native.GetClipboardData(13);
                if (handle == IntPtr.Zero)
                    return null;
                var pointer = Native.GlobalLock(handle);
                try
                {
                    return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    Native.GlobalUnlock(handle);
                }
            }
            finally
            {
                Native.CloseClipboard();
            }
        }

        private static bool OpenClipboardRetry()
        {
            for (var i = 0; i < 10; i++)
            {
                if (Native.OpenClipboard(IntPtr.Zero))
                    return true;
                Thread.Sleep(20);
            }
            return false;
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct MOUSEINPUT
            {
                public int Dx, Dy;
                public uint MouseData, Flags, Time;
                public IntPtr ExtraInfo;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct KEYBDINPUT
            {
                public ushort Vk, Scan;
                public uint Flags, Time;
                public IntPtr ExtraInfo;
            }

            [StructLayout(LayoutKind.Explicit)]
            public struct InputUnion
            {
                [FieldOffset(0)] public MOUSEINPUT Mouse;
                [FieldOffset(0)] public KEYBDINPUT Keyboard;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct INPUT
            {
                public uint Type;
                public InputUnion U;
            }

            [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool BlockInput(bool block);
            [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
            [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr owner);
            [DllImport("user32.dll")] public static extern bool CloseClipboard();
            [DllImport("user32.dll")] public static extern bool EmptyClipboard();
            [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr memory);
            [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
            [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
            [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr memory);
            [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr memory);
        }
    }

    /// <summary>Escritorio que recibe la entrada (Default, Winlogon, UAC) y unir el hilo actual a él.</summary>
    public static class DesktopSwitcher
    {
        public static string InputDesktopName()
        {
            var desktop = Native.OpenInputDesktop(0, false, 0x0001);
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

        public static bool SwitchThreadTo(string name)
        {
            if (Name(Native.GetThreadDesktop(Native.GetCurrentThreadId())) == name)
                return true;
            var desktop = Native.OpenDesktopW(name, 0, false, 0x10000000);
            if (desktop == IntPtr.Zero)
                return false;
            if (Native.SetThreadDesktop(desktop))
                return true;
            Native.CloseDesktop(desktop);
            return false;
        }

        private static string Name(IntPtr desktop)
        {
            var buffer = new char[256];
            return Native.GetUserObjectInformationW(desktop, 2, buffer, buffer.Length * 2, out var needed)
                ? new string(buffer, 0, Math.Max(0, needed / 2 - 1))
                : null;
        }

        private static class Native
        {
            [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr OpenDesktopW(string name, uint flags, bool inherit, uint access);
            [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
            [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
            [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint threadId);
            [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool GetUserObjectInformationW(IntPtr handle, int index, [Out] char[] info, int length, out int needed);
        }
    }

    /// <summary>Ctrl+Alt+Supr con SendSAS (Windows 7 o posterior), con la política SoftwareSASGeneration.</summary>
    public static class SecureAttention
    {
        public static bool Send()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                {
                    if (!(key.GetValue("SoftwareSASGeneration") is int value) || (value & 1) == 0)
                        key.SetValue("SoftwareSASGeneration", 1, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                Native.SendSAS(false);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return false;
            }
        }

        private static class Native
        {
            [DllImport("sas.dll")] public static extern void SendSAS(bool asUser);
        }
    }

    /// <summary>Cambiar de usuario, cerrar la sesión de Windows o reiniciar (menú "Más" del visor).</summary>
    public static class SystemActions
    {
        public static string Run(string action)
        {
            switch (action)
            {
                case "cambiar_usuario":
                    return Native.WTSDisconnectSession(IntPtr.Zero, -1, false) ? null : "Windows no permitió cambiar de usuario.";
                case "cerrar_sesion":
                    return Native.WTSLogoffSession(IntPtr.Zero, -1, false) ? null : "Windows no permitió cerrar la sesión.";
                case "reiniciar":
                    using (var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                               "/r /t 15 /c \"Soporte de TI reiniciará este equipo en 15 segundos.\" /d p:4:1") { UseShellExecute = false, CreateNoWindow = true }))
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
}

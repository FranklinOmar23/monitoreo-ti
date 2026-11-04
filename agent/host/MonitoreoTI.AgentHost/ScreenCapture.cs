using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MonitoreoTI.AgentHost;

/// <summary>Un monitor en coordenadas del escritorio virtual (píxeles físicos: el asistente es "per-monitor DPI aware").</summary>
public sealed record MonitorArea(int X, int Y, int Width, int Height, bool Primary);

/// <summary>De dónde sale la pantalla que se manda al visor: V1 (GDI + JPEG) o V2 (DXGI + H.264).</summary>
public interface IScreenSource : IDisposable
{
    MonitorArea Area { get; }

    /// <summary>La próxima captura manda la imagen completa (V1) o un cuadro clave (V2).</summary>
    void RequestFull();

    /// <summary>El mensaje binario para el visor, o null si nada cambió. quality: 10 a 95 (más es mejor imagen).</summary>
    byte[]? Capture(int quality);
}

/// <summary>
/// Pantalla V1 (sección 10.2): captura con GDI (BitBlt) un monitor en un mapa de bits, dibuja el cursor, compara con la
/// captura anterior por mosaicos de <see cref="Tile"/> píxeles y codifica en JPEG (GDI+ de Windows) solo lo que cambió.
/// Los mosaicos contiguos de una misma fila se envían como un solo rectángulo.
/// </summary>
public sealed unsafe class ScreenCapture : IScreenSource
{
    public const int Tile = 64;
    public const byte TilesFrame = 1;

    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _bits;
    private byte[] _previous = [];
    private MonitorArea _area;
    private bool _forceFull = true;

    public ScreenCapture(MonitorArea area)
    {
        _screenDc = Native.GetDC(IntPtr.Zero);
        _memoryDc = Native.CreateCompatibleDC(_screenDc);
        _area = area;
        Allocate();
        Jpeg.Start();
    }

    public MonitorArea Area => _area;

    public void SwitchTo(MonitorArea area)
    {
        _area = area;
        Allocate();
    }

    /// <summary>La próxima captura manda la pantalla completa (al empezar, al cambiar de monitor o si el visor lo pide).</summary>
    public void RequestFull() => _forceFull = true;

    private int Stride => _area.Width * 4;

    private void Allocate()
    {
        if (_bitmap != IntPtr.Zero)
            Native.DeleteObject(_bitmap);
        var info = new Native.BitmapInfoHeader
        {
            Size = Marshal.SizeOf<Native.BitmapInfoHeader>(),
            Width = _area.Width,
            Height = -_area.Height, // de arriba hacia abajo
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = Native.CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo reservar la memoria de la captura.");
        Native.SelectObject(_memoryDc, _bitmap);
        _previous = new byte[Stride * _area.Height];
        _forceFull = true;
    }

    /// <summary>
    /// Captura y devuelve el mensaje binario con lo que cambió, o null si nada cambió. Formato (little endian):
    /// [tipo:1 = 1][ancho:2][alto:2][rectángulos:2] y por cada uno [x:2][y:2][ancho:2][alto:2][largo:4][JPEG].
    /// </summary>
    public byte[]? Capture(int quality)
    {
        if (!Native.BitBlt(_memoryDc, 0, 0, _area.Width, _area.Height, _screenDc, _area.X, _area.Y, Native.SrcCopy | Native.CaptureBlt))
            return null; // escritorio que este proceso no puede leer: se reintenta en la próxima vuelta
        DrawCursor(_memoryDc, _area);

        var current = new Span<byte>((void*)_bits, Stride * _area.Height);
        var rects = Changed(current, _forceFull);
        _forceFull = false;
        if (rects.Count == 0)
            return null;
        current.CopyTo(_previous);

        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[7];
        header[0] = TilesFrame;
        BinaryPrimitives.WriteUInt16LittleEndian(header[1..], (ushort)_area.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[3..], (ushort)_area.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[5..], (ushort)rects.Count);
        output.Write(header);

        Span<byte> rectHeader = stackalloc byte[12];
        foreach (var (x, y, w, h) in rects)
        {
            var jpeg = Jpeg.Encode(_bits + y * Stride + x * 4, w, h, Stride, quality);
            BinaryPrimitives.WriteUInt16LittleEndian(rectHeader, (ushort)x);
            BinaryPrimitives.WriteUInt16LittleEndian(rectHeader[2..], (ushort)y);
            BinaryPrimitives.WriteUInt16LittleEndian(rectHeader[4..], (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(rectHeader[6..], (ushort)h);
            BinaryPrimitives.WriteInt32LittleEndian(rectHeader[8..], jpeg.Length);
            output.Write(rectHeader);
            output.Write(jpeg);
        }
        return output.ToArray();
    }

    /// <summary>Rectángulos que cambiaron: por fila de mosaicos, cada tramo de mosaicos seguidos que difieren.</summary>
    private List<(int X, int Y, int W, int H)> Changed(ReadOnlySpan<byte> current, bool full)
    {
        var result = new List<(int, int, int, int)>();
        var columns = (_area.Width + Tile - 1) / Tile;
        var rows = (_area.Height + Tile - 1) / Tile;
        for (var ty = 0; ty < rows; ty++)
        {
            var y = ty * Tile;
            var h = Math.Min(Tile, _area.Height - y);
            var start = -1;
            for (var tx = 0; tx <= columns; tx++)
            {
                var changed = tx < columns && (full || TileChanged(current, tx * Tile, y, Math.Min(Tile, _area.Width - tx * Tile), h));
                if (changed && start < 0)
                    start = tx;
                else if (!changed && start >= 0)
                {
                    var x = start * Tile;
                    result.Add((x, y, Math.Min(tx * Tile, _area.Width) - x, h));
                    start = -1;
                }
            }
        }
        return result;
    }

    private bool TileChanged(ReadOnlySpan<byte> current, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
        {
            var offset = row * Stride + x * 4;
            if (!current.Slice(offset, w * 4).SequenceEqual(_previous.AsSpan(offset, w * 4)))
                return true;
        }
        return false;
    }

    /// <summary>Posición y forma del cursor (para saber si se movió), o null si no se ve.</summary>
    internal static (int X, int Y, IntPtr Handle)? CursorState()
    {
        var info = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
        return Native.GetCursorInfo(ref info) && (info.Flags & 1) != 0 && info.Cursor != IntPtr.Zero ? (info.X, info.Y, info.Cursor) : null;
    }

    /// <summary>Dibuja el cursor en el contexto (la captura de Windows no lo incluye).</summary>
    internal static void DrawCursor(IntPtr dc, MonitorArea area)
    {
        var info = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
        if (!Native.GetCursorInfo(ref info) || (info.Flags & 1) == 0 || info.Cursor == IntPtr.Zero)
            return;
        if (!Native.GetIconInfo(info.Cursor, out var icon))
            return;
        Native.DrawIconEx(dc, info.X - icon.HotspotX - area.X, info.Y - icon.HotspotY - area.Y, info.Cursor, 0, 0, 0, IntPtr.Zero, 3);
        if (icon.Mask != IntPtr.Zero)
            Native.DeleteObject(icon.Mask);
        if (icon.Color != IntPtr.Zero)
            Native.DeleteObject(icon.Color);
    }

    public static List<MonitorArea> Monitors()
    {
        var list = new List<MonitorArea>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
            if (Native.GetMonitorInfo(monitor, ref info))
            {
                var r = info.Monitor;
                list.Add(new MonitorArea(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.Flags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        // El principal primero.
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.X).ToList();
    }

    public void Dispose()
    {
        if (_bitmap != IntPtr.Zero)
            Native.DeleteObject(_bitmap);
        Native.DeleteDC(_memoryDc);
        Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }

    /// <summary>JPEG con GDI+ (incluido en Windows): sin librerías de imagen.</summary>
    private static class Jpeg
    {
        private static readonly Guid JpegEncoder = new("557cf401-1a04-11d3-9a73-0000f81ef32e");
        private static readonly Guid QualityParameter = new("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb");
        private const int PixelFormat32bppRgb = 0x00022009;
        private static bool _started;

        public static void Start()
        {
            if (_started)
                return;
            var input = new StartupInput { Version = 1 };
            if (GdiplusStartup(out _, ref input, IntPtr.Zero) != 0)
                throw new InvalidOperationException("No se pudo iniciar GDI+.");
            _started = true;
        }

        public static byte[] Encode(IntPtr scan0, int width, int height, int stride, int quality)
        {
            if (GdipCreateBitmapFromScan0(width, height, stride, PixelFormat32bppRgb, scan0, out var bitmap) != 0)
                throw new InvalidOperationException("No se pudo preparar la imagen.");
            // EncoderParameters en x64: Count (4) + relleno (4) + EncoderParameter { GUID (16), NumberOfValues (4), Type (4), Value (8) }.
            var parameters = Marshal.AllocHGlobal(40);
            var value = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(value, Math.Clamp(quality, 10, 95));
                Marshal.WriteInt32(parameters, 1);
                Marshal.WriteInt32(parameters, 4, 0);
                Marshal.StructureToPtr(QualityParameter, parameters + 8, false);
                Marshal.WriteInt32(parameters, 24, 1);
                Marshal.WriteInt32(parameters, 28, 4); // EncoderParameterValueTypeLong
                Marshal.WriteIntPtr(parameters, 32, value);

                if (CreateStreamOnHGlobal(IntPtr.Zero, true, out var stream) != 0)
                    throw new InvalidOperationException("No se pudo crear el flujo de la imagen.");
                var encoder = JpegEncoder;
                if (GdipSaveImageToStream(bitmap, stream, ref encoder, parameters) != 0)
                    throw new InvalidOperationException("No se pudo codificar la imagen.");

                stream.Stat(out var stat, 1);
                var bytes = new byte[stat.cbSize];
                stream.Seek(0, 0, IntPtr.Zero);
                stream.Read(bytes, bytes.Length, IntPtr.Zero);
                Marshal.ReleaseComObject(stream);
                return bytes;
            }
            finally
            {
                Marshal.FreeHGlobal(value);
                Marshal.FreeHGlobal(parameters);
                GdipDisposeImage(bitmap);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInput
        {
            public int Version;
            public IntPtr Callback;
            public int SuppressBackgroundThread;
            public int SuppressExternalCodecs;
        }

        [DllImport("gdiplus.dll")]
        private static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);

        [DllImport("gdiplus.dll")]
        private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

        [DllImport("gdiplus.dll")]
        private static extern int GdipSaveImageToStream(IntPtr image, IStream stream, ref Guid encoder, IntPtr parameters);

        [DllImport("gdiplus.dll")]
        private static extern int GdipDisposeImage(IntPtr image);

        [DllImport("ole32.dll")]
        private static extern int CreateStreamOnHGlobal(IntPtr global, bool deleteOnRelease, out IStream stream);
    }

    internal static class Native
    {
        public const int SrcCopy = 0x00CC0020;
        public const int CaptureBlt = 0x40000000;

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
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    }
}

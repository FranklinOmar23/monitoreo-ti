using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Pantalla V2 (sección 10.2, etapa 7.5): copia el monitor con la duplicación de escritorio de DXGI (la tarjeta de video
/// entrega solo cuando algo cambió), dibuja el cursor, convierte a NV12 y lo comprime en H.264 con el codificador de
/// Media Foundation de Windows. Mucho menos ancho de banda que los mosaicos JPEG con la imagen en movimiento.
/// <para>
/// Si algo falla (equipo sin aceleración, sesión de escritorio remoto, Windows sin el codificador) se lanza la
/// excepción y el hilo de la pantalla sigue con la V1.
/// </para>
/// </summary>
public sealed unsafe class VideoScreenCapture : IScreenSource
{
    /// <summary>1 = mosaicos JPEG (V1); el 2 lo usan las partes de archivo del explorador.</summary>
    public const byte VideoFrame = 3;
    public const int FramesPerSecond = 30;

    private readonly MonitorArea _area;
    private readonly int _width;
    private readonly int _height;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private ID3D11Texture2D? _staging;
    private readonly byte[] _clean;
    private readonly byte[] _nv12;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _bits;
    private H264Encoder _encoder;
    private bool _hasImage;
    private bool _forceKey = true;
    private (int X, int Y, IntPtr Handle)? _cursor;
    private readonly long _start = Environment.TickCount64;
    private int _quality = 60;
    private long _lastRateChange;

    public VideoScreenCapture(MonitorArea area)
    {
        _area = area;
        // NV12 necesita ancho y alto pares; si el monitor tiene uno impar se deja fuera la última columna o fila.
        _width = area.Width & ~1;
        _height = area.Height & ~1;
        if (_width < 64 || _height < 64)
            throw new NotSupportedException("Pantalla demasiado chica para video.");

        (_device, _context, _duplication) = Duplicate(area);
        _clean = new byte[area.Width * area.Height * 4];
        _nv12 = new byte[_width * _height * 3 / 2];

        _screenDc = ScreenCapture.Native.GetDC(IntPtr.Zero);
        _memoryDc = ScreenCapture.Native.CreateCompatibleDC(_screenDc);
        var info = new ScreenCapture.Native.BitmapInfoHeader
        {
            Size = Marshal.SizeOf<ScreenCapture.Native.BitmapInfoHeader>(),
            Width = area.Width,
            Height = -area.Height,
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = ScreenCapture.Native.CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
            throw new InvalidOperationException("No se pudo reservar la memoria de la captura.");
        ScreenCapture.Native.SelectObject(_memoryDc, _bitmap);

        _encoder = new H264Encoder(_width, _height, FramesPerSecond, Bitrate(_quality));
    }

    public MonitorArea Area => _area;

    public void RequestFull() => _forceKey = true;

    /// <summary>
    /// Formato (little endian): [tipo:1 = 3][ancho:2][alto:2][clave:1 (1 = cuadro clave)] y el H.264 en Annex B
    /// (con SPS y PPS antes de cada cuadro clave): el visor lo decodifica con WebCodecs.
    /// </summary>
    public byte[]? Capture(int quality)
    {
        var desktopChanged = AcquireFrame();
        var cursor = ScreenCapture.CursorState();
        var cursorChanged = cursor != _cursor;
        _cursor = cursor;
        if (!_hasImage || (!desktopChanged && !cursorChanged && !_forceKey))
            return null;

        AdjustBitrate(quality);
        // Imagen limpia + cursor: el cursor se dibuja sobre una copia (si solo se movió el cursor, no hay que recapturar).
        Marshal.Copy(_clean, 0, _bits, _clean.Length);
        ScreenCapture.DrawCursor(_memoryDc, _area);
        Nv12.FromBgra(new ReadOnlySpan<byte>((void*)_bits, _clean.Length), _area.Width * 4, _width, _height, _nv12);

        var key = _forceKey;
        _forceKey = false;
        var timestamp = (Environment.TickCount64 - _start) * 10_000;
        var encoded = _encoder.Encode(_nv12, timestamp, key);
        if (encoded is null || encoded.Length == 0)
            return null;

        var message = new byte[6 + encoded.Length];
        message[0] = VideoFrame;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(1), (ushort)_width);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(3), (ushort)_height);
        message[5] = H264Encoder.HasKeyFrame(encoded) ? (byte)1 : (byte)0;
        encoded.CopyTo(message, 6);
        return message;
    }

    /// <summary>Calidad (10 a 95) → bits por segundo: 35 ≈ 1,2 Mb/s, 60 ≈ 2,5 Mb/s y 85 ≈ 5 Mb/s en 1920 × 1080.</summary>
    public static uint BitrateFor(int width, int height, int quality) =>
        (uint)Math.Clamp(width * (double)height * 0.6 * Math.Pow(2, (Math.Clamp(quality, 10, 95) - 35) / 25.0), 250_000, 20_000_000);

    private uint Bitrate(int quality) => BitrateFor(_width, _height, quality);

    /// <summary>
    /// La calidad automática cambia de a poco: el codificador se ajusta en el momento si lo permite; si no, se vuelve a
    /// crear (empieza con un cuadro clave), y entonces solo ante cambios grandes y no más de una vez cada 5 segundos.
    /// </summary>
    private void AdjustBitrate(int quality)
    {
        if (quality == _quality)
            return;
        var target = Bitrate(quality);
        var change = Math.Abs(target - (double)_encoder.Bitrate) / _encoder.Bitrate;
        var now = Environment.TickCount64;
        if (_encoder.CanChangeBitrate)
        {
            if (change < 0.1 || now - _lastRateChange < 1000)
                return;
            _encoder.SetBitrate(target);
        }
        else
        {
            if (change < 0.4 || now - _lastRateChange < 5000)
                return;
            _encoder.Dispose();
            _encoder = new H264Encoder(_width, _height, FramesPerSecond, target);
            _forceKey = true;
        }
        _quality = quality;
        _lastRateChange = now;
    }

    /// <summary>Copia el nuevo cuadro del escritorio a la imagen limpia. false si no cambió.</summary>
    private bool AcquireFrame()
    {
        var result = _duplication.AcquireNextFrame(0, out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        // Acceso perdido (cambió el escritorio, la resolución o entró una aplicación a pantalla completa): se lanza y
        // el hilo de la pantalla vuelve a crear la captura.
        result.CheckError();
        try
        {
            if (info.LastPresentTime == 0)
                return false; // solo se movió el cursor
            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            if (_staging is null)
            {
                var description = texture.Description;
                description.Usage = ResourceUsage.Staging;
                description.CPUAccessFlags = CpuAccessFlags.Read;
                description.BindFlags = BindFlags.None;
                description.MiscFlags = ResourceOptionFlags.None;
                description.MipLevels = 1;
                description.ArraySize = 1;
                _staging = _device.CreateTexture2D(in description);
            }
            _context.CopyResource(_staging, texture);
            var mapped = _context.Map(_staging, 0);
            try
            {
                var row = _area.Width * 4;
                fixed (byte* clean = _clean)
                {
                    for (var y = 0; y < _area.Height; y++)
                        Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch, clean + y * row, row, row);
                }
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }
            _hasImage = true;
            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>La salida de video (monitor) que corresponde al área, con un dispositivo D3D11 en su tarjeta.</summary>
    private static (ID3D11Device, ID3D11DeviceContext, IDXGIOutputDuplication) Duplicate(MonitorArea area)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var r = d.DesktopCoordinates;
                        if (r.Left != area.X || r.Top != area.Y || r.Right - r.Left != area.Width || r.Bottom - r.Top != area.Height)
                            continue;
                        // Un monitor girado entrega la imagen sin girar: queda para la V1.
                        if (d.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            throw new NotSupportedException("Monitor girado.");

                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                            out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
                        try
                        {
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            return (device!, context!, output1.DuplicateOutput(device));
                        }
                        catch
                        {
                            context?.Dispose();
                            device?.Dispose();
                            throw;
                        }
                    }
                }
            }
        }
        throw new NotSupportedException("No se encontró el monitor en la tarjeta de video.");
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _staging?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
        ScreenCapture.Native.DeleteObject(_bitmap);
        ScreenCapture.Native.DeleteDC(_memoryDc);
        ScreenCapture.Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}

/// <summary>BGRA (lo que entrega Windows) → NV12 (lo que pide el codificador), con la matriz BT.709 en rango limitado.</summary>
public static class Nv12
{
    public static void FromBgra(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        if (nv12.Length < width * height * 3 / 2)
            throw new ArgumentException("Búfer NV12 chico.", nameof(nv12));
        unsafe
        {
            fixed (byte* source = bgra)
            fixed (byte* target = nv12)
            {
                var src = source;
                var dst = target;
                // Por pares de filas: cada par da dos filas de Y y una de U/V intercalados (submuestreo 2 × 2).
                Parallel.For(0, height / 2, pair =>
                {
                    var y0 = pair * 2;
                    var row0 = src + y0 * stride;
                    var row1 = row0 + stride;
                    var luma0 = dst + y0 * width;
                    var luma1 = luma0 + width;
                    var chroma = dst + width * height + pair * width;
                    for (var x = 0; x < width; x += 2)
                    {
                        int r = 0, g = 0, b = 0;
                        for (var i = 0; i < 2; i++)
                        {
                            var p0 = row0 + (x + i) * 4;
                            var p1 = row1 + (x + i) * 4;
                            luma0[x + i] = Luma(p0[2], p0[1], p0[0]);
                            luma1[x + i] = Luma(p1[2], p1[1], p1[0]);
                            r += p0[2] + p1[2];
                            g += p0[1] + p1[1];
                            b += p0[0] + p1[0];
                        }
                        r = (r + 2) >> 2;
                        g = (g + 2) >> 2;
                        b = (b + 2) >> 2;
                        chroma[x] = (byte)((-26 * r - 87 * g + 112 * b + 128 >> 8) + 128);
                        chroma[x + 1] = (byte)((112 * r - 102 * g - 10 * b + 128 >> 8) + 128);
                    }
                });
            }
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)((47 * r + 157 * g + 16 * b + 128 >> 8) + 16);
}

/// <summary>
/// Codificador H.264 de Media Foundation (incluido en Windows): perfil base, sin cuadros B y en modo de baja latencia,
/// así cada cuadro sale apenas entra (un cuadro en el visor nunca espera al siguiente).
/// </summary>
public sealed unsafe class H264Encoder : IDisposable
{
    private const int BaselineProfile = 66;
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int StreamChange = unchecked((int)0xC00D6D61);   // MF_E_TRANSFORM_STREAM_CHANGE
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // MF_LOW_LATENCY
    private static readonly object StartGate = new();
    private static bool _started;

    private readonly IMFTransform _transform;
    private readonly ICodecAPI? _codec;
    private readonly int _frameSize;
    private readonly long _duration;

    public H264Encoder(int width, int height, int fps, uint bitrate)
    {
        lock (StartGate)
        {
            if (!_started)
            {
                MediaFactory.MFStartup(true).CheckError();
                _started = true;
            }
        }
        Bitrate = bitrate;
        _frameSize = width * height * 3 / 2;
        _duration = 10_000_000 / fps;

        _transform = Create();
        try
        {
            _codec = CodecApi.From(_transform);
            if (_codec is not null)
            {
                CodecApi.Set(_codec, CodecApi.RateControlMode, 0u); // CBR: un ancho de banda parejo
                CodecApi.Set(_codec, CodecApi.LowLatencyMode, true);
                CodecApi.Set(_codec, CodecApi.BPictureCount, 0u);
                CodecApi.Set(_codec, CodecApi.QualityVsSpeed, 0u); // lo más rápido
                CodecApi.Set(_codec, CodecApi.GopSize, (uint)(fps * 10)); // un cuadro clave cada 10 s por si algo se pierde
            }
            try
            {
                _transform.Attributes.Set(LowLatency, true);
            }
            catch (SharpGenException)
            {
                // Algunos codificadores no aceptan atributos: el modo de baja latencia ya va por ICodecAPI.
            }

            using var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progresivo
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, (uint)BaselineProfile);
            _transform.SetOutputType(0, output, 0);

            using var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(fps, 1));
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            _transform.SetInputType(0, input, 0);

            if (_codec is not null)
                CanChangeBitrate = CodecApi.Set(_codec, CodecApi.MeanBitRate, bitrate);
            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public uint Bitrate { get; private set; }

    /// <summary>El codificador acepta cambiar el ancho de banda sin volver a empezar.</summary>
    public bool CanChangeBitrate { get; }

    public void SetBitrate(uint bitrate)
    {
        if (_codec is not null && CodecApi.Set(_codec, CodecApi.MeanBitRate, bitrate))
            Bitrate = bitrate;
    }

    /// <summary>El H.264 (Annex B) de un cuadro NV12; null si el codificador todavía no devolvió nada.</summary>
    public byte[]? Encode(byte[] nv12, long timestamp, bool keyFrame)
    {
        if (keyFrame && _codec is not null)
            CodecApi.Set(_codec, CodecApi.ForceKeyFrame, 1u);

        using (var buffer = MediaFactory.MFCreateMemoryBuffer(_frameSize))
        using (var sample = MediaFactory.MFCreateSample())
        {
            buffer.Lock(out var data, out _, out _);
            try
            {
                Marshal.Copy(nv12, 0, data, _frameSize);
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.CurrentLength = _frameSize;
            sample.AddBuffer(buffer);
            sample.SampleTime = timestamp;
            sample.SampleDuration = _duration;
            _transform.ProcessInput(0, sample, 0);
        }

        using var result = new MemoryStream();
        while (true)
        {
            var info = _transform.GetOutputStreamInfo(0);
            var provides = (info.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
            IMFSample? own = null;
            if (!provides)
            {
                own = MediaFactory.MFCreateSample();
                using var outBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, _frameSize));
                own.AddBuffer(outBuffer);
            }
            var output = new OutputDataBuffer { StreamID = 0, Sample = own! };
            var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            try
            {
                if (hr.Code == NeedMoreInput)
                    break;
                if (hr.Code == StreamChange)
                {
                    // El codificador cambió el formato de salida (pasa con algunos): se acepta el que propone.
                    using var type = _transform.GetOutputAvailableType(0, 0);
                    _transform.SetOutputType(0, type, 0);
                    continue;
                }
                hr.CheckError();
                using var contiguous = output.Sample.ConvertToContiguousBuffer();
                contiguous.Lock(out var data, out _, out var length);
                try
                {
                    result.Write(new ReadOnlySpan<byte>((void*)data, length));
                }
                finally
                {
                    contiguous.Unlock();
                }
            }
            finally
            {
                output.Events?.Dispose();
                if (output.Sample is not null && !ReferenceEquals(output.Sample, own))
                    output.Sample.Dispose();
                own?.Dispose();
            }
        }
        return result.Length == 0 ? null : result.ToArray();
    }

    /// <summary>¿Hay un cuadro IDR (NAL tipo 5) en el Annex B?</summary>
    public static bool HasKeyFrame(ReadOnlySpan<byte> annexB)
    {
        for (var i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] != 0 || annexB[i + 1] != 0)
                continue;
            var start = annexB[i + 2] == 1 ? i + 3 : annexB[i + 2] == 0 && annexB[i + 3] == 1 ? i + 4 : -1;
            if (start < 0 || start >= annexB.Length)
                continue;
            if ((annexB[start] & 0x1F) == 5)
                return true;
            i = start - 1;
        }
        return false;
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>El primer codificador H.264 sincrónico de NV12 que ofrece Windows (el de Microsoft si no hay otro).</summary>
    private static IMFTransform Create()
    {
        var flags = (uint)(EnumFlag.EnumFlagSyncmft | EnumFlag.EnumFlagLocalmft | EnumFlag.EnumFlagSortandfilter);
        using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags,
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 });
        foreach (var activate in found)
        {
            try
            {
                return activate.ActivateObject<IMFTransform>();
            }
            catch (SharpGenException)
            {
                // Ese no se pudo crear: se prueba el siguiente.
            }
        }
        throw new NotSupportedException("Windows no tiene un codificador H.264.");
    }

    public void Dispose()
    {
        if (_codec is not null)
            Marshal.ReleaseComObject(_codec);
        _transform.Dispose();
    }
}

/// <summary>
/// ICodecAPI (codecapi.h): los ajustes del codificador (ancho de banda, baja latencia, cuadro clave a pedido). Vortice
/// no lo trae; se usa por la interoperabilidad COM de .NET. Cada ajuste es "si se puede": devuelve si lo aceptó.
/// </summary>
internal static class CodecApi
{
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid Iid = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    public static ICodecAPI? From(IMFTransform transform)
    {
        var iid = Iid;
        if (Marshal.QueryInterface(transform.NativePointer, ref iid, out var pointer) != 0 || pointer == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.GetObjectForIUnknown(pointer) as ICodecAPI;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public static bool Set(ICodecAPI codec, Guid api, object value)
    {
        try
        {
            return codec.SetValue(ref api, ref value) >= 0;
        }
        catch (COMException)
        {
            return false;
        }
    }
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    // El orden de la tabla de COM importa: los que no se usan van con firmas que no se llaman.
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, IntPtr min, IntPtr max, IntPtr step);
    [PreserveSig] int GetParameterValues(ref Guid api, IntPtr values, IntPtr count);
    [PreserveSig] int GetDefaultValue(ref Guid api, IntPtr value);
    [PreserveSig] int GetValue(ref Guid api, IntPtr value);
    [PreserveSig] int SetValue(ref Guid api, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>
/// Diagnóstico para soporte ("prueba-pantalla"): captura el monitor principal unos segundos con la V2 y dice si funciona,
/// cuánto tarda cada cuadro y cuánto ocupa. Con un archivo, guarda el video H.264 para revisarlo.
/// </summary>
public static class ScreenDiagnostics
{
    public static int Run(string? file, TextWriter output)
    {
        ScreenCapture.Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        var monitor = ScreenCapture.Monitors().FirstOrDefault();
        if (monitor is null)
        {
            output.WriteLine("No se encontró ninguna pantalla.");
            return 1;
        }
        output.WriteLine($"Monitor principal: {monitor.Width} × {monitor.Height}");
        VideoScreenCapture capture;
        try
        {
            capture = new VideoScreenCapture(monitor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            output.WriteLine($"Pantalla V2 no disponible ({ex.GetType().Name}: {ex.Message}). Se usará la V1 (JPEG).");
            return 3;
        }

        using (capture)
        using (var stream = file is null ? null : File.Create(file))
        {
            int frames = 0, keys = 0;
            long bytes = 0, encodeMs = 0;
            var until = Environment.TickCount64 + 3000;
            capture.RequestFull();
            while (Environment.TickCount64 < until)
            {
                var started = Environment.TickCount64;
                var frame = capture.Capture(60);
                if (frame is not null)
                {
                    encodeMs += Environment.TickCount64 - started;
                    frames++;
                    bytes += frame.Length;
                    if (frame[5] == 1)
                        keys++;
                    stream?.Write(frame, 6, frame.Length - 6);
                }
                // Que algo cambie: el cursor se mueve en círculo.
                var angle = (Environment.TickCount64 % 2000) / 2000.0 * Math.PI * 2;
                ScreenCapture.Native.SetCursorPos(monitor.X + monitor.Width / 2 + (int)(200 * Math.Cos(angle)), monitor.Y + monitor.Height / 2 + (int)(200 * Math.Sin(angle)));
                Thread.Sleep(1000 / VideoScreenCapture.FramesPerSecond);
            }
            output.WriteLine($"Pantalla V2 (DXGI + H.264): {frames} cuadros en 3 s, {keys} clave, {bytes / 1024} KB en total, {(frames == 0 ? 0 : encodeMs / frames)} ms por cuadro.");
            return frames > 0 && keys > 0 ? 0 : 4;
        }
    }
}

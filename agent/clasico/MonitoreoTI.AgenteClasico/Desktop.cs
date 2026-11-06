using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Mensajes entre el servicio y el asistente por sus tuberías: [tipo:1][largo:4 LE][datos]; 1 = texto JSON, 2 = binario.
    /// El mismo formato que el agente normal.
    /// </summary>
    public static class FrameIo
    {
        public const byte Text = 1;
        public const byte Binary = 2;
        private const int MaxFrame = 16 * 1024 * 1024;

        public sealed class Frame
        {
            public byte Type;
            public byte[] Data;
        }

        public static Frame Read(Stream stream)
        {
            var header = ReadExactly(stream, 5);
            if (header == null)
                return null;
            var length = BitConverter.ToInt32(header, 1);
            if (length < 0 || length > MaxFrame)
                return null;
            var data = length == 0 ? new byte[0] : ReadExactly(stream, length);
            return data == null ? null : new Frame { Type = header[0], Data = data };
        }

        public static void Write(Stream stream, object gate, byte type, byte[] data)
        {
            var header = new byte[5];
            header[0] = type;
            Buffer.BlockCopy(BitConverter.GetBytes(data.Length), 0, header, 1, 4);
            lock (gate)
            {
                stream.Write(header, 0, 5);
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
        }

        public static void WriteText(Stream stream, object gate, object message) =>
            Write(stream, gate, Text, Encoding.UTF8.GetBytes(Json.Write(message)));

        private static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n == 0)
                    return null;
                read += n;
            }
            return buffer;
        }
    }

    /// <summary>
    /// Escritorio remoto del lado del servicio (decisión 84): el servicio corre en la sesión 0 y no ve la pantalla del
    /// usuario, así que abre el asistente ("MonitoreoTI.AgenteClasico.exe asistente") en la sesión de la consola con su
    /// propio token de SYSTEM (así también entra a la pantalla de bloqueo y a la de UAC) y pasa los mensajes tal cual.
    /// Los pedidos del explorador de archivos dentro del escritorio ({op, id}) los atiende el servicio.
    /// </summary>
    public sealed class DesktopSession : ISessionHandler
    {
        private const byte FilePart = 2;
        private readonly SessionChannel _channel;
        private readonly Action<Dictionary<string, object>> _report;
        private readonly FileSession _files;
        private readonly object _gate = new object();
        private SafeProcess _process;
        private FileStream _toHelper;
        private FileStream _fromHelper;
        private bool _disposed;

        private static readonly Dictionary<string, Func<Dictionary<string, object>, string>> Audited = new Dictionary<string, Func<Dictionary<string, object>, string>>
        {
            ["accion"] = m => new[] { "cambiar_usuario", "cerrar_sesion", "reiniciar" }.Contains(m.Str("accion")) ? m.Str("accion") : null,
            ["bloquear_entrada"] = m => m.Bool("activo") ? "bloquear_entrada" : "desbloquear_entrada",
            ["pantalla_negra"] = m => m.Bool("activo") ? "pantalla_negra" : "quitar_pantalla_negra",
        };

        public DesktopSession(SessionChannel channel, Action<Dictionary<string, object>> report)
        {
            _channel = channel;
            _report = report;
            _files = channel.AllowFiles
                ? new FileSession(channel, report, data =>
                {
                    var wrapped = new byte[data.Length + 1];
                    wrapped[0] = FilePart;
                    Buffer.BlockCopy(data, 0, wrapped, 1, data.Length);
                    return wrapped;
                })
                : null;
            try
            {
                Start();
            }
            catch (Exception ex)
            {
                Fail($"No se pudo abrir el escritorio remoto: {ex.Message}");
                return;
            }
            FrameIo.WriteText(_toHelper, _gate, new Dictionary<string, object>
            {
                ["tipo"] = "inicio",
                ["tecnico"] = channel.Technician,
                ["pedirPermiso"] = channel.AskConsent,
                ["soloVer"] = channel.ViewOnly,
            });
            Task.Run(() => Forward());
            Log.Info($"Escritorio remoto abierto (sesión {channel.SessionId}).");
        }

        private void Start()
        {
            var session = Native.WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF)
                throw new InvalidOperationException("el equipo no tiene una consola activa");
            Native.WTSQueryUserToken(session, out var userToken);

            var inherit = new Native.SecurityAttributes { Length = Marshal.SizeOf(typeof(Native.SecurityAttributes)), InheritHandle = true };
            if (!Native.CreatePipe(out var childStdin, out var toChild, ref inherit, 0) || !Native.CreatePipe(out var fromChild, out var childStdout, ref inherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.SetHandleInformation(toChild, 1, 0);
            Native.SetHandleInformation(fromChild, 1, 0);

            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x000F01FF, out var own)
                || !Native.DuplicateTokenEx(own, 0x02000000, IntPtr.Zero, 2, 1, out var primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.CloseHandle(own);
            var environment = IntPtr.Zero;
            try
            {
                if (!Native.SetTokenInformation(primary, 12, ref session, sizeof(uint))) // TokenSessionId
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (userToken != IntPtr.Zero)
                    Native.CreateEnvironmentBlock(out environment, userToken, false);
                var exe = Process.GetCurrentProcess().MainModule.FileName;
                var startup = new Native.StartupInfo
                {
                    Size = Marshal.SizeOf(typeof(Native.StartupInfo)),
                    Desktop = "winsta0\\default",
                    Flags = 0x00000100 | 0x00000001, // USESTDHANDLES | USESHOWWINDOW
                    ShowWindow = 0,
                    StdInput = childStdin.DangerousGetHandle(),
                    StdOutput = childStdout.DangerousGetHandle(),
                    StdError = IntPtr.Zero,
                };
                var commandLine = new StringBuilder($"\"{exe}\" asistente");
                if (!Native.CreateProcessAsUserW(primary, exe, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                        0x00000400 | 0x08000000, environment, Path.GetDirectoryName(exe), ref startup, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                Native.CloseHandle(info.Thread);
                _process = new SafeProcess(info.Process);
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    Native.DestroyEnvironmentBlock(environment);
                if (userToken != IntPtr.Zero)
                    Native.CloseHandle(userToken);
                Native.CloseHandle(primary);
                childStdin.Dispose();
                childStdout.Dispose();
            }
            _toHelper = new FileStream(toChild, FileAccess.Write, 1);
            _fromHelper = new FileStream(fromChild, FileAccess.Read, 64 * 1024);
        }

        /// <summary>Del asistente al visor, tal cual.</summary>
        private void Forward()
        {
            try
            {
                FrameIo.Frame frame;
                while ((frame = FrameIo.Read(_fromHelper)) != null)
                {
                    if (frame.Type == FrameIo.Text)
                        _channel.SendText(Encoding.UTF8.GetString(frame.Data));
                    else if (frame.Type == FrameIo.Binary)
                        _channel.SendBinary(frame.Data);
                }
            }
            catch (Exception)
            {
                // El asistente terminó.
            }
            if (!_disposed)
            {
                Log.Info("Escritorio remoto terminado.");
                _channel.Close();
            }
        }

        public void OnText(string text)
        {
            var message = Json.Read(text);
            // Pedidos del explorador de archivos ({op, id}): los atiende el servicio, no el asistente.
            if (message?.Str("op") != null)
            {
                if (_files != null)
                    _files.OnText(text);
                else
                    _channel.SendText(Json.Write(new Dictionary<string, object> { ["id"] = message.Int("id"), ["ok"] = false, ["error"] = "No tiene permiso para transferir archivos." }));
                return;
            }
            var tipo = message?.Str("tipo");
            if (tipo != null && Audited.TryGetValue(tipo, out var audit) && !_channel.ViewOnly)
            {
                var action = audit(message);
                if (action != null)
                    _report(new Dictionary<string, object> { ["accion"] = action });
            }
            try
            {
                if (_toHelper != null)
                    FrameIo.Write(_toHelper, _gate, FrameIo.Text, Encoding.UTF8.GetBytes(text));
            }
            catch (IOException)
            {
                // El asistente terminó: Forward cierra la sesión.
            }
        }

        public void OnBinary(byte[] data)
        {
            // Del visor, el binario es una parte de un archivo que se sube: [2][id:4][datos].
            if (_files != null && data.Length > 0 && data[0] == FilePart)
            {
                var part = new byte[data.Length - 1];
                Buffer.BlockCopy(data, 1, part, 0, part.Length);
                _files.OnBinary(part);
            }
        }

        private void Fail(string text)
        {
            _channel.SendText(Json.Write(new Dictionary<string, object> { ["tipo"] = "error", ["mensaje"] = text }));
            _channel.Close();
        }

        public void Dispose()
        {
            _disposed = true;
            _files?.Dispose();
            // Al cerrarse su entrada, el asistente quita el aviso y termina; si no lo hace, se fuerza.
            try
            {
                _toHelper?.Dispose();
            }
            catch (IOException)
            {
            }
            var process = _process;
            _process = null;
            if (process != null)
            {
                Task.Run(() =>
                {
                    if (!process.WaitForExit(5000))
                        process.Kill();
                    process.Dispose();
                });
            }
        }

        private sealed class SafeProcess : IDisposable
        {
            private readonly IntPtr _handle;
            public SafeProcess(IntPtr handle) => _handle = handle;
            public bool WaitForExit(int ms) => Native.WaitForSingleObject(_handle, (uint)ms) == 0;
            public void Kill() => Native.TerminateProcess(_handle, 1);
            public void Dispose() => Native.CloseHandle(_handle);
        }

        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct SecurityAttributes
            {
                public int Length;
                public IntPtr Descriptor;
                public bool InheritHandle;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct StartupInfo
            {
                public int Size;
                public string Reserved, Desktop, Title;
                public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
                public short ShowWindow, Reserved2;
                public IntPtr Reserved3, StdInput, StdOutput, StdError;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct ProcessInformation
            {
                public IntPtr Process, Thread;
                public int ProcessId, ThreadId;
            }

            [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
            [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
            [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
            [DllImport("advapi32.dll", SetLastError = true)] public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint value, int length);
            [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(uint session, out IntPtr token);
            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);
            [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
            [DllImport("userenv.dll")] public static extern bool DestroyEnvironmentBlock(IntPtr environment);
            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes,
                IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation info);
            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
            [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
            [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
            [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr process, uint code);
        }
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Lanzador del asistente ("MonitoreoTI.AgentHost.exe escritorio", sección 8.1): el servicio corre como SYSTEM en la
/// sesión 0, que no ve el escritorio del usuario. Este proceso abre el asistente en la sesión de la consola con el
/// token de SYSTEM (su propio token, con la sesión cambiada: solo así puede entrar al escritorio seguro de la pantalla
/// de bloqueo y de UAC, 7.4) y con conductos propios, y pasa los mensajes en ambos sentidos tal cual (FrameIo).
/// Funciona aunque nadie haya iniciado sesión (pantalla de inicio). Cuando el agente cierra stdin, todo termina.
/// </summary>
public static class SessionLauncher
{
    public static async Task<int> RunAsync(AgentPaths paths)
    {
        using var stdout = Console.OpenStandardOutput();
        var gate = new object();

        var session = Native.WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
        {
            Fail(stdout, gate, "El equipo no tiene una consola activa (¿está en una conexión de Escritorio remoto de Windows?).");
            return 3;
        }
        // El entorno del usuario si hay alguien conectado (variables, carpetas); si no, el del sistema.
        Native.WTSQueryUserToken(session, out var userToken);

        Native.Process process;
        SafeFileHandle toChild, fromChild;
        try
        {
            (process, toChild, fromChild) = Start(session, userToken, paths.HostExe);
        }
        catch (Win32Exception ex)
        {
            Fail(stdout, gate, $"No se pudo abrir el asistente en la sesión del usuario: {ex.Message}");
            return 4;
        }
        finally
        {
            if (userToken != IntPtr.Zero)
                Native.CloseHandle(userToken);
        }

        await using var input = new FileStream(toChild, FileAccess.Write, 1);
        await using var output = new FileStream(fromChild, FileAccess.Read, 1);
        using var stdin = Console.OpenStandardInput();

        // Del agente al asistente y del asistente al agente, sin interpretar.
        var down = Task.Run(async () =>
        {
            try
            {
                await stdin.CopyToAsync(input);
            }
            catch (IOException)
            {
                // El asistente terminó.
            }
            input.Close(); // el asistente ve el fin de su entrada y termina
        });
        var up = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            int n;
            try
            {
                while ((n = await output.ReadAsync(buffer)) > 0)
                {
                    lock (gate)
                    {
                        stdout.Write(buffer, 0, n);
                        stdout.Flush();
                    }
                }
            }
            catch (IOException)
            {
                // El agente se fue.
            }
        });

        var exited = Task.Run(() => process.WaitForExit());
        await Task.WhenAny(exited, down);
        // Si el agente cerró primero, el asistente tiene unos segundos para irse solo.
        if (!exited.IsCompleted && !exited.Wait(TimeSpan.FromSeconds(3)))
            process.Kill();
        await Task.WhenAny(up, Task.Delay(1000));
        var code = process.ExitCode;
        process.Dispose();
        return code;
    }

    private static void Fail(Stream stdout, object gate, string message) =>
        FrameIo.Write(stdout, gate, FrameIo.Text, JsonSerializer.SerializeToUtf8Bytes(new { tipo = "error", mensaje = message }));

    private static (Native.Process Process, SafeFileHandle ToChild, SafeFileHandle FromChild) Start(uint session, IntPtr userToken, string hostExe)
    {
        var inherit = new Native.SecurityAttributes { Length = Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = true };
        if (!Native.CreatePipe(out var childStdin, out var toChild, ref inherit, 0) || !Native.CreatePipe(out var fromChild, out var childStdout, ref inherit, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // Nuestros extremos no se heredan.
        Native.SetHandleInformation(toChild, 1, 0);
        Native.SetHandleInformation(fromChild, 1, 0);

        // Token de SYSTEM (el de este proceso) llevado a la sesión de la consola. Cambiar la sesión exige SeTcbPrivilege,
        // que SYSTEM tiene.
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x000F01FF, out var own) // TOKEN_ALL_ACCESS
            || !Native.DuplicateTokenEx(own, 0x02000000, IntPtr.Zero, 2, 1, out var primary)) // MAXIMUM_ALLOWED, impersonation, primario
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Native.CloseHandle(own);
        if (!Native.SetTokenInformation(primary, 12, ref session, sizeof(uint))) // TokenSessionId
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var environment = IntPtr.Zero;
        if (userToken != IntPtr.Zero)
            Native.CreateEnvironmentBlock(out environment, userToken, false);
        try
        {
            var startup = new Native.StartupInfo
            {
                Size = Marshal.SizeOf<Native.StartupInfo>(),
                Desktop = "winsta0\\default",
                Flags = 0x00000100 | 0x00000001, // USESTDHANDLES | USESHOWWINDOW
                ShowWindow = 0,
                StdInput = childStdin.DangerousGetHandle(),
                StdOutput = childStdout.DangerousGetHandle(),
                StdError = IntPtr.Zero,
            };
            var commandLine = new StringBuilder($"\"{hostExe}\" asistente");
            if (!Native.CreateProcessAsUserW(primary, hostExe, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    0x00000400 | 0x08000000, environment, Path.GetDirectoryName(hostExe), ref startup, out var info)) // UNICODE_ENVIRONMENT | NO_WINDOW
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Native.CloseHandle(info.Thread);
            return (new Native.Process(info.Process), toChild, fromChild);
        }
        finally
        {
            if (environment != IntPtr.Zero)
                Native.DestroyEnvironmentBlock(environment);
            Native.CloseHandle(primary);
            childStdin.Dispose();
            childStdout.Dispose();
        }
    }

    private static class Native
    {
        public sealed class Process(IntPtr handle) : IDisposable
        {
            public void WaitForExit() => WaitForSingleObject(handle, 0xFFFFFFFF);
            public void Kill() => TerminateProcess(handle, 1);
            public int ExitCode => GetExitCodeProcess(handle, out var code) ? (int)code : -1;
            public void Dispose() => CloseHandle(handle);
        }

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
            public string? Reserved, Desktop, Title;
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
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint value, int length);
        [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(uint session, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);
        [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
        [DllImport("userenv.dll")] public static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfo startup,
            out ProcessInformation info);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);
        [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr process, uint code);
    }
}

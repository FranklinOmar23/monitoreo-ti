using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Terminal remota (sección 10.2): PowerShell o CMD dentro de un pseudoconsola de Windows (ConPTY,
/// CreatePseudoConsole). La lanza el agente con "MonitoreoTI.AgentHost.exe pty --shell powershell --cols 120 --rows 30".
/// <para>
/// stdin trae mensajes con encabezado: [tipo:1][largo:4, little endian][datos]. Tipo 1: lo que escribe el técnico
/// (bytes UTF-8); tipo 2: nuevo tamaño (columnas:2, filas:2). stdout lleva la salida de la consola tal cual (VT/ANSI).
/// Al terminar el shell, el proceso sale con su código; si se cierra stdin (el técnico se fue), se termina el shell.
/// </para>
/// </summary>
public static class PtyHost
{
    public const byte InputFrame = 1;
    public const byte ResizeFrame = 2;
    private const int MaxFrame = 1024 * 1024;

    public static async Task<int> RunAsync(string[] args)
    {
        var shell = Arg(args, "--shell") == "cmd" ? "cmd" : "powershell";
        var cols = short.TryParse(Arg(args, "--cols"), out var c) && c is > 0 and < 1000 ? c : (short)120;
        var rows = short.TryParse(Arg(args, "--rows"), out var r) && r is > 0 and < 500 ? r : (short)30;

        using var terminal = PseudoConsole.Start(CommandLine(shell), cols, rows);
        using var stdout = Console.OpenStandardOutput();
        using var stdin = Console.OpenStandardInput();

        var output = terminal.Output.CopyToAsync(stdout, 16 * 1024).ContinueWith(_ => { }, TaskScheduler.Default);
        var input = Task.Run(() => PumpInputAsync(stdin, terminal));
        var exited = terminal.WaitForExitAsync();

        var first = await Task.WhenAny(exited, input);
        if (first == input && !exited.IsCompleted)
            terminal.Kill();

        var code = await exited;
        // Cerrar la pseudoconsola vacía su salida y cierra el conducto: la copia termina sola.
        terminal.ClosePseudoConsole();
        await Task.WhenAny(output, Task.Delay(TimeSpan.FromSeconds(2)));
        return code;
    }

    /// <summary>Lee los mensajes de stdin hasta que se cierre (el agente cortó la sesión).</summary>
    private static async Task PumpInputAsync(Stream stdin, PseudoConsole terminal)
    {
        var header = new byte[5];
        try
        {
            while (await ReadExactlyAsync(stdin, header))
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                if (length is < 0 or > MaxFrame)
                    return;
                var payload = new byte[length];
                if (!await ReadExactlyAsync(stdin, payload))
                    return;

                switch (header[0])
                {
                    case InputFrame:
                        await terminal.Input.WriteAsync(payload);
                        await terminal.Input.FlushAsync();
                        break;
                    case ResizeFrame when length == 4:
                        terminal.Resize(BinaryPrimitives.ReadInt16LittleEndian(payload), BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(2)));
                        break;
                }
            }
        }
        catch (IOException)
        {
            // El shell terminó y se cerró su entrada.
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read));
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }

    private static string CommandLine(string shell)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return shell == "cmd"
            ? $"\"{Path.Combine(system, "cmd.exe")}\""
            : $"\"{Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")}\" -NoLogo";
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>Un proceso dentro de una pseudoconsola: su entrada, su salida y su tamaño.</summary>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _console;
    private readonly IntPtr _process;
    private readonly IntPtr _thread;

    public FileStream Input { get; }
    public FileStream Output { get; }

    private PseudoConsole(IntPtr console, IntPtr process, IntPtr thread, FileStream input, FileStream output)
    {
        _console = console;
        _process = process;
        _thread = thread;
        Input = input;
        Output = output;
    }

    public static PseudoConsole Start(string commandLine, short cols, short rows)
    {
        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)
            || !Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudieron crear los conductos de la terminal.");

        var hr = Native.CreatePseudoConsole(new Native.Coord(cols, rows), inputRead, outputWrite, 0, out var console);
        if (hr != 0)
            throw new Win32Exception(hr, "Este Windows no admite pseudoconsolas (se necesita Windows 10 1809 o posterior).");
        // La pseudoconsola tiene su copia de estos extremos; los nuestros se cierran para detectar el fin.
        inputRead.Dispose();
        outputWrite.Dispose();

        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !Native.UpdateProcThreadAttribute(attributes, 0, Native.ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo preparar la pseudoconsola.");

            var startup = new Native.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<Native.StartupInfoEx>();
            // Sin esto, el shell heredaría los stdin/stdout del anfitrión (conductos del agente) y saltaría la pseudoconsola.
            startup.StartupInfo.dwFlags = Native.StartfUseStdHandles;

            var directory = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, Native.ExtendedStartupInfoPresent,
                    IntPtr.Zero, directory, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo iniciar el shell.");

            return new PseudoConsole(console, info.hProcess, info.hThread,
                new FileStream(inputWrite, FileAccess.Write, 1), new FileStream(outputRead, FileAccess.Read, 1));
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_console != IntPtr.Zero && cols > 0 && rows > 0)
            Native.ResizePseudoConsole(_console, new Native.Coord(cols, rows));
    }

    public Task<int> WaitForExitAsync() => Task.Run(() =>
    {
        Native.WaitForSingleObject(_process, Native.Infinite);
        return Native.GetExitCodeProcess(_process, out var code) ? (int)code : -1;
    });

    public void Kill() => Native.TerminateProcess(_process, 1);

    public void ClosePseudoConsole()
    {
        if (_console == IntPtr.Zero)
            return;
        Native.ClosePseudoConsole(_console);
        _console = IntPtr.Zero;
    }

    public void Dispose()
    {
        ClosePseudoConsole();
        Input.Dispose();
        Output.Dispose();
        Native.CloseHandle(_thread);
        Native.CloseHandle(_process);
    }

    private static class Native
    {
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const int StartfUseStdHandles = 0x00000100;
        public const uint Infinite = 0xFFFFFFFF;
        public static readonly IntPtr ProcThreadAttributePseudoConsole = (IntPtr)0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Coord(short x, short y)
        {
            public readonly short X = x;
            public readonly short Y = y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr console, Coord size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr console);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation info);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint code);

        [DllImport("kernel32.dll")]
        public static extern bool TerminateProcess(IntPtr process, uint code);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}

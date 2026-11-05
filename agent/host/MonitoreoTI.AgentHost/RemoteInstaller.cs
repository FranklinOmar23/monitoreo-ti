using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Instalación remota del agente ("MonitoreoTI.AgentHost.exe instalar-remoto", decisión 77). Lo llama el agente
/// recolector con la petición JSON por stdin (con la clave: nunca va en la línea de comandos) y va escribiendo una línea
/// JSON por paso en stdout: {"estado":"conectando"} … y al final {"fin":true,"estado":"instalado"|"error"|"omitido"}.
///
/// Todo va por SMB (puerto 445), lo mismo que usa PsExec: se abre \\IP\ADMIN$ con la cuenta del dominio, se copian el
/// instalador y el certificado, y el administrador de servicios remoto (por la misma conexión) crea un servicio de un
/// solo uso que lanza el instalador como SYSTEM en silencio. Luego se espera a que aparezca el servicio del agente y se
/// limpia lo copiado. La clave solo vive en memoria y no se escribe en ningún lado.
/// </summary>
public static class RemoteInstaller
{
    private const string TempServiceName = "CentinelaInstalar";
    private const string RemoteFolder = @"Temp\Centinela";
    private const string SetupName = "MonitoreoTI-Agente-Setup.exe";
    private const string CaName = "MonitoreoTI-CA.pem";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        InstallRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<InstallRequest>(await input.ReadToEndAsync(), Json);
        }
        catch (JsonException)
        {
            request = null;
        }
        void Step(string estado, string? detalle = null, string? sistema = null, bool fin = false)
        {
            output.WriteLine(JsonSerializer.Serialize(new InstallStep { Estado = estado, Detalle = detalle, Sistema = sistema, Fin = fin ? true : null }, Json));
            output.Flush();
        }

        if (request is null || !System.Net.IPAddress.TryParse(request.Ip, out _) || string.IsNullOrEmpty(request.Usuario)
            || string.IsNullOrEmpty(request.Clave) || !File.Exists(request.Instalador) || string.IsNullOrEmpty(request.Servidor)
            || string.IsNullOrEmpty(request.Token) || !SafeArgument(request.Servidor) || !SafeArgument(request.Token))
        {
            Step("error", "Petición de instalación inválida.", fin: true);
            return 2;
        }

        var share = $@"\\{request.Ip}\ADMIN$";
        var connected = false;
        string? sistema = null;
        try
        {
            Step("conectando");
            if (!await PortOpenAsync(request.Ip, 445, TimeSpan.FromSeconds(5)))
            {
                Step("error", "No responde por el puerto 445 (Compartir archivos): está apagado, el firewall de Windows lo bloquea o no es Windows.", fin: true);
                return 1;
            }
            Native.WNetCancelConnection2(share, 0, true);
            var resource = new Native.NetResource { Type = Native.ResourceTypeDisk, RemoteName = share };
            var code = Native.WNetAddConnection2(ref resource, request.Clave, request.Usuario, Native.ConnectTemporary);
            if (code != 0)
            {
                Step("error", ConnectionError(code), fin: true);
                return 1;
            }
            connected = true;

            var (os, supported, reason) = Describe(share);
            sistema = os;
            if (!supported)
            {
                Step("omitido", reason, sistema, fin: true);
                return 0;
            }
            if (ServiceExists(request.Ip, Installation.ServiceName))
            {
                Step("omitido", "Ya tiene el agente instalado.", sistema, fin: true);
                return 0;
            }

            Step("copiando", null, sistema);
            var folder = Path.Combine(share, RemoteFolder);
            Directory.CreateDirectory(folder);
            File.Copy(request.Instalador, Path.Combine(folder, SetupName), overwrite: true);
            if (!string.IsNullOrEmpty(request.Ca) && File.Exists(request.Ca))
                File.Copy(request.Ca, Path.Combine(folder, CaName), overwrite: true);

            Step("instalando", null, sistema);
            LaunchAsSystem(request.Ip, CommandLine(request.Servidor, request.Token));

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Clamp(request.EsperaMinutos ?? 10, 2, 30));
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (ServiceExists(request.Ip, Installation.ServiceName))
                {
                    Step("instalado", "El servicio del agente quedó instalado; falta que se registre en el servidor.", sistema, fin: true);
                    return 0;
                }
                // El instalador en curso tiene el archivo abierto: si se puede borrar, ya terminó (sin el servicio).
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(40) && TryDelete(Path.Combine(folder, SetupName)))
                {
                    Step("error", "El instalador terminó pero el servicio del agente no quedó instalado. Revise el registro en C:\\ProgramData\\MonitoreoTI\\Agent de ese equipo.", sistema, fin: true);
                    return 1;
                }
            }
            Step("error", "El instalador no terminó a tiempo.", sistema, fin: true);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Step("error", $"No se pudo instalar: {ex.Message}", sistema, fin: true);
            return 1;
        }
        finally
        {
            if (connected)
            {
                DeleteService(request.Ip, TempServiceName);
                for (var i = 0; i < 6 && Directory.Exists(Path.Combine(share, RemoteFolder)); i++)
                {
                    if (TryDeleteDirectory(Path.Combine(share, RemoteFolder)))
                        break;
                    Thread.Sleep(5000);
                }
                Native.WNetCancelConnection2(share, 0, true);
            }
        }
    }

    /// <summary>Solo letras, números y lo que trae una URL o un token: nada que el intérprete de comandos interprete.</summary>
    public static bool SafeArgument(string value) =>
        value.Length is > 0 and <= 300 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_.:/=+~".Contains(c));

    /// <summary>
    /// cmd lanza el instalador y termina enseguida: el administrador de servicios no lo espera ni lo corta (cmd no es un
    /// servicio de verdad). %SystemRoot% lo expande Windows en el equipo destino.
    /// </summary>
    public static string CommandLine(string server, string token) =>
        $"%SystemRoot%\\System32\\cmd.exe /c start \"\" \"%SystemRoot%\\{RemoteFolder}\\{SetupName}\" /S /SERVER={server} /TOKEN={token}";

    public static string ConnectionError(int code) => code switch
    {
        1326 => "Usuario o clave incorrectos.",
        5 => "La cuenta no es administrador de ese equipo.",
        53 or 67 => "No se encontró el recurso ADMIN$: el equipo no comparte sus recursos administrativos.",
        1219 => "El recolector ya está conectado a ese equipo con otra cuenta; inténtelo en unos minutos.",
        1909 => "La cuenta está bloqueada.",
        1331 => "La cuenta está deshabilitada.",
        1907 or 1330 => "La clave de la cuenta venció o hay que cambiarla.",
        1311 => "No hay controlador de dominio disponible para validar la cuenta.",
        1385 => "La cuenta no tiene permiso para iniciar sesión por red en ese equipo.",
        _ => $"No se pudo conectar a ADMIN$: {new Win32Exception(code).Message} ({code}).",
    };

    /// <summary>Nombre del sistema según la versión de su kernel32.dll; el agente necesita Windows 10 u 11 de 64 bits.</summary>
    public static (string Name, bool Supported, string? Reason) DescribeVersion(int major, int minor, int build, bool is64)
    {
        var name = (major, minor) switch
        {
            (10, _) when build >= 22000 => "Windows 11",
            (10, _) => "Windows 10",
            (6, 3) => "Windows 8.1",
            (6, 2) => "Windows 8",
            (6, 1) => "Windows 7",
            (6, 0) => "Windows Vista",
            _ => $"Windows {major}.{minor}",
        };
        var full = $"{name} ({major}.{minor}.{build}) {(is64 ? "64" : "32")} bits";
        if (major < 10)
            return (full, false, $"{name}: el agente actual necesita Windows 10 o superior.");
        if (!is64)
            return (full, false, "Windows de 32 bits: el agente es de 64 bits.");
        return (full, true, null);
    }

    private static (string Name, bool Supported, string? Reason) Describe(string share)
    {
        var kernel = Path.Combine(share, "System32", "kernel32.dll");
        if (!File.Exists(kernel))
            return ("Desconocido", false, "No se pudo leer la versión de Windows del equipo.");
        var info = FileVersionInfo.GetVersionInfo(kernel);
        return DescribeVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, Directory.Exists(Path.Combine(share, "SysWOW64")));
    }

    private static async Task<bool> PortOpenAsync(string ip, int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool ServiceExists(string ip, string name)
    {
        var manager = Native.OpenSCManager($@"\\{ip}", null, Native.ScManagerConnect);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo abrir el administrador de servicios del equipo");
        try
        {
            var service = Native.OpenService(manager, name, Native.ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return false;
            Native.CloseServiceHandle(service);
            return true;
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }
    }

    private static void LaunchAsSystem(string ip, string commandLine)
    {
        var manager = Native.OpenSCManager($@"\\{ip}", null, Native.ScManagerConnect | Native.ScManagerCreateService);
        if (manager == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo abrir el administrador de servicios del equipo");
        try
        {
            DeleteService(ip, TempServiceName);
            var service = Native.CreateService(manager, TempServiceName, "Centinela: instalación del agente", Native.ServiceAllAccess,
                Native.ServiceWin32OwnProcess, Native.ServiceDemandStart, Native.ServiceErrorIgnore, commandLine,
                null, IntPtr.Zero, null, null, null);
            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo crear el servicio temporal de instalación");
            try
            {
                // cmd no responde como servicio: Windows informa 1053 o 1067, pero el instalador ya quedó corriendo.
                if (!Native.StartService(service, 0, null))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is not (1053 or 1067))
                        throw new Win32Exception(error, "No se pudo iniciar el instalador");
                }
            }
            finally
            {
                Native.DeleteService(service);
                Native.CloseServiceHandle(service);
            }
        }
        finally
        {
            Native.CloseServiceHandle(manager);
        }
    }

    private static void DeleteService(string ip, string name)
    {
        var manager = Native.OpenSCManager($@"\\{ip}", null, Native.ScManagerConnect);
        if (manager == IntPtr.Zero)
            return;
        var service = Native.OpenService(manager, name, Native.Delete);
        if (service != IntPtr.Zero)
        {
            Native.DeleteService(service);
            Native.CloseServiceHandle(service);
        }
        Native.CloseServiceHandle(manager);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static class Native
    {
        public const int ResourceTypeDisk = 1;
        public const int ConnectTemporary = 4;
        public const uint ScManagerConnect = 0x0001;
        public const uint ScManagerCreateService = 0x0002;
        public const uint ServiceQueryStatus = 0x0004;
        public const uint ServiceAllAccess = 0xF01FF;
        public const uint Delete = 0x10000;
        public const uint ServiceWin32OwnProcess = 0x10;
        public const uint ServiceDemandStart = 3;
        public const uint ServiceErrorIgnore = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NetResource
        {
            public int Scope;
            public int Type;
            public int DisplayType;
            public int Usage;
            public string? LocalName;
            public string RemoteName;
            public string? Comment;
            public string? Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
        public static extern int WNetAddConnection2(ref NetResource resource, string password, string username, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
        public static extern int WNetCancelConnection2(string name, int flags, bool force);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
        public static extern IntPtr OpenSCManager(string machine, string? database, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
        public static extern IntPtr OpenService(IntPtr manager, string name, uint access);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateServiceW")]
        public static extern IntPtr CreateService(IntPtr manager, string name, string displayName, uint access, uint type, uint start,
            uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceW")]
        public static extern bool StartService(IntPtr service, int argCount, string[]? args);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}

public sealed class InstallRequest
{
    public string Ip { get; set; } = "";
    public string Usuario { get; set; } = "";
    public string Clave { get; set; } = "";

    /// <summary>Ruta local del instalador ya verificado (SHA-256 y firma) por el agente.</summary>
    public string Instalador { get; set; } = "";
    public string? Ca { get; set; }
    public string Servidor { get; set; } = "";
    public string Token { get; set; } = "";
    public int? EsperaMinutos { get; set; }
}

public sealed class InstallStep
{
    public string Estado { get; set; } = "";
    public string? Detalle { get; set; }
    public string? Sistema { get; set; }
    public bool? Fin { get; set; }
}

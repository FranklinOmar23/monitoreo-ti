using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>Agente clásico (decisión 78): viaja junto a este anfitrión en el instalador del agente.</summary>
    public const string ClassicName = "MonitoreoTI.AgenteClasico.exe";

    private static string ClassicPath => Path.Combine(AppContext.BaseDirectory, ClassicName);

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

        // Toda autenticación de red (archivos y servicios) va con la cuenta del dominio, como "runas /netonly": el
        // recolector corre como SYSTEM y, sin esto, Windows usa la cuenta de máquina, que no es administradora del equipo.
        // Localmente sigue siendo SYSTEM (lee el instalador de su carpeta). LogonUser no valida la clave: eso lo hace la
        // conexión a ADMIN$ y da el error claro.
        var (domain, user) = SplitAccount(request.Usuario);
        if (!Native.LogonUser(user, domain, request.Clave, Native.LogonNewCredentials, Native.ProviderWinNt50, out var token))
        {
            Step("error", $"No se pudo usar la cuenta: {new Win32Exception(Marshal.GetLastWin32Error()).Message}", fin: true);
            return 1;
        }
        using (token)
            return await WindowsIdentity.RunImpersonatedAsync(token, () => InstallAsync(request, Step));
    }

    /// <summary>"DOMINIO\\usuario" → (DOMINIO, usuario); "usuario@dominio" va entero con dominio null.</summary>
    public static (string? Domain, string User) SplitAccount(string account)
    {
        var slash = account.IndexOf('\\');
        return slash > 0 ? (account[..slash], account[(slash + 1)..]) : (null, account);
    }

    private static async Task<int> InstallAsync(InstallRequest request, Action<string, string?, string?, bool> step)
    {
        void Step(string estado, string? detalle = null, string? sistema = null, bool fin = false) => step(estado, detalle, sistema, fin);
        var share = $@"\\{request.Ip}\ADMIN$";
        var ipc = $@"\\{request.Ip}\IPC$";
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
            // 64: el equipo cortó la sesión SMB. A veces es pasajero (otra sesión que se cerraba): un reintento.
            if (code == NetNameDeleted)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                code = Native.WNetAddConnection2(ref resource, request.Clave, request.Usuario, Native.ConnectTemporary);
            }
            if (code != 0)
            {
                Step("error", ConnectionError(code), fin: true);
                return 1;
            }
            connected = true;

            // El administrador de servicios remoto va por \\IP\IPC$. Sin conectarlo con la cuenta, Windows usa la del
            // recolector (corre como SYSTEM: la cuenta de máquina), que no es administradora del equipo destino.
            Native.WNetCancelConnection2(ipc, 0, true);
            var ipcResource = new Native.NetResource { Type = Native.ResourceTypeAny, RemoteName = ipc };
            code = Native.WNetAddConnection2(ref ipcResource, request.Clave, request.Usuario, Native.ConnectTemporary);
            if (code != 0)
            {
                Step("error", "IPC$: " + ConnectionError(code), fin: true);
                return 1;
            }

            var (os, kind, reason) = Describe(share, File.Exists(ClassicPath));
            sistema = os;
            if (kind is null)
            {
                Step("omitido", reason, sistema, fin: true);
                return 0;
            }
            var classic = kind == AgentKind.Classic;
            // Un clásico más viejo que el que trae este agente se actualiza (conserva su registro): así lo instalado a
            // distancia queda igual a lo instalado a mano (decisión 84).
            var updateClassic = false;
            if (ServiceExists(request.Ip, Installation.ServiceName))
            {
                var installed = classic ? InstalledClassicVersion(share) : null;
                if (installed is null || installed >= BundledClassicVersion())
                {
                    Step("omitido", "Ya tiene el agente instalado.", sistema, fin: true);
                    return 0;
                }
                updateClassic = true;
            }

            Step("copiando", null, sistema);
            var folder = Path.Combine(share, RemoteFolder);
            Directory.CreateDirectory(folder);
            var setupName = classic ? ClassicName : SetupName;
            File.Copy(classic ? ClassicPath : request.Instalador, Path.Combine(folder, setupName), overwrite: true);
            if (!string.IsNullOrEmpty(request.Ca) && File.Exists(request.Ca))
                File.Copy(request.Ca, Path.Combine(folder, CaName), overwrite: true);

            Step("instalando", null, sistema);
            LaunchAsSystem(request.Ip, updateClassic
                ? ClassicUpdateCommandLine()
                : classic ? ClassicCommandLine(request.Servidor, request.Token) : CommandLine(request.Servidor, request.Token));

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Clamp(request.EsperaMinutos ?? 10, 2, 30));
            var started = DateTime.UtcNow;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (updateClassic)
                {
                    // El servicio ya existía: terminó cuando suelta el archivo copiado.
                    if (DateTime.UtcNow - started < TimeSpan.FromSeconds(10) || !TryDelete(Path.Combine(folder, setupName)))
                        continue;
                    var now = InstalledClassicVersion(share);
                    if (now is not null && now >= BundledClassicVersion())
                    {
                        Step("instalado", $"Se actualizó el agente clásico a la versión {now.ToString(3)}: ya tiene escritorio remoto, terminal y archivos.", sistema, fin: true);
                        return 0;
                    }
                    Step("error", ClassicFailure(share), sistema, fin: true);
                    return 1;
                }
                if (ServiceExists(request.Ip, Installation.ServiceName))
                {
                    Step("instalado", classic
                        ? "Se instaló el agente clásico (Windows 7); falta que se conecte al servidor."
                        : "El servicio del agente quedó instalado; falta que se registre en el servidor.", sistema, fin: true);
                    return 0;
                }
                // El instalador en curso tiene el archivo abierto: si se puede borrar, ya terminó (sin el servicio).
                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(classic ? 20 : 40) && TryDelete(Path.Combine(folder, setupName)))
                {
                    Step("error", classic
                        ? ClassicFailure(share)
                        : "El instalador terminó pero el servicio del agente no quedó instalado. Revise el registro en C:\\ProgramData\\MonitoreoTI\\Agent de ese equipo.", sistema, fin: true);
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
                Native.WNetCancelConnection2(ipc, 0, true);
            }
        }
    }

    private static Win32Exception ScmFailure(string what)
    {
        var code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, ServiceManagerError(code, what));
    }

    /// <summary>Error del administrador de servicios remoto con el código de Windows y qué revisar.</summary>
    public static string ServiceManagerError(int code, string what) => code switch
    {
        5 => $"{what}: acceso denegado (5). La cuenta no es administradora de ese equipo o UAC bloquea la administración remota.",
        1722 or 1753 or 1727 => $"{what}: el equipo no responde por RPC ({code}). Revise en su firewall «Administración remota de servicios».",
        1060 => $"{what}: el servicio no existe (1060).",
        1073 => $"{what}: ya existe un servicio con ese nombre (1073).",
        _ => $"{what}: {new Win32Exception(code).Message} ({code}).",
    };

    /// <summary>Solo letras, números y lo que trae una URL o un token: nada que el intérprete de comandos interprete.</summary>
    public static bool SafeArgument(string value) =>
        value.Length is > 0 and <= 300 && value.All(c => char.IsAsciiLetterOrDigit(c) || "-_.:/=+~".Contains(c));

    /// <summary>
    /// cmd lanza el instalador y termina enseguida: el administrador de servicios no lo espera ni lo corta (cmd no es un
    /// servicio de verdad). %SystemRoot% lo expande Windows en el equipo destino.
    /// </summary>
    public static string CommandLine(string server, string token) =>
        $"%SystemRoot%\\System32\\cmd.exe /c start \"\" \"%SystemRoot%\\{RemoteFolder}\\{SetupName}\" /S /SERVER={server} /TOKEN={token}";

    /// <summary>El agente clásico es su propio instalador: se registra con el token y crea el servicio.</summary>
    public static string ClassicCommandLine(string server, string token) =>
        $"%SystemRoot%\\System32\\cmd.exe /c start \"\" \"%SystemRoot%\\{RemoteFolder}\\{ClassicName}\" instalar /SERVER={server} /TOKEN={token}";

    /// <summary>Sin token: el clásico ya registrado solo reemplaza su programa y reinicia el servicio.</summary>
    public static string ClassicUpdateCommandLine() =>
        $"%SystemRoot%\\System32\\cmd.exe /c start \"\" \"%SystemRoot%\\{RemoteFolder}\\{ClassicName}\" instalar";

    private static Version BundledClassicVersion() => FileVersion(ClassicPath) ?? new Version(0, 0);

    /// <summary>Versión del clásico instalado en el equipo (por C$), o null si no se puede leer.</summary>
    private static Version? InstalledClassicVersion(string share) =>
        FileVersion(Path.Combine(share.Replace("ADMIN$", "C$"), "Program Files", "MonitoreoTI", "AgenteClasico", ClassicName));

    private static Version? FileVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>La última línea con error del registro del agente clásico en el equipo (dice por qué no se instaló).</summary>
    private static string ClassicFailure(string share)
    {
        // ADMIN$ es C:\Windows y no deja subir de carpeta: el registro se lee por C$.
        var log = Path.Combine(share.Replace("ADMIN$", "C$"), "ProgramData", "MonitoreoTI", "Agent", "agente-clasico.log");
        try
        {
            var last = File.Exists(log) ? File.ReadLines(log).LastOrDefault(l => l.Contains(" ERR ")) : null;
            if (last is not null)
                return $"El agente clásico no se instaló: {last[(last.IndexOf(" ERR ", StringComparison.Ordinal) + 5)..]}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return "El agente clásico no se instaló. Revise C:\\ProgramData\\MonitoreoTI\\Agent\\agente-clasico.log en ese equipo.";
    }

    private const int NetNameDeleted = 64;

    public static string ConnectionError(int code) => code switch
    {
        NetNameDeleted or 59 => $"El equipo aceptó la conexión por el puerto 445 pero la cortó ({code}). Suele ser un equipo que solo habla SMB1 (Windows XP, una impresora, un NAS o un punto de venta viejo), un antivirus o firewall que corta las conexiones de archivos, o que no es una PC con Windows.",
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

    public enum AgentKind
    {
        Modern,
        Classic,
    }

    /// <summary>
    /// Qué agente le corresponde según la versión de su kernel32.dll: el normal en Windows 10/11 de 64 bits; el clásico
    /// en Windows 7 SP1, 8, 8.1 o de 32 bits (si viene con este anfitrión y el equipo tiene .NET Framework 4.5.2).
    /// null con el motivo si ninguno sirve.
    /// </summary>
    public static (string Name, AgentKind? Kind, string? Reason) DescribeVersion(int major, int minor, int build, bool is64, bool hasClassic = false, bool? dotNet452 = null)
    {
        // Hay Windows 10 cuyo kernel32.dll dice 6.2 en la versión numérica; el número de compilación no miente
        // (Windows 10 empieza en la 10240).
        if (major < 10 && build >= 10240)
            (major, minor) = (10, 0);
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
        if (major >= 10 && is64)
            return (full, AgentKind.Modern, null);
        var why = major < 10 ? $"{name}: el agente normal necesita Windows 10 o superior" : "Windows de 32 bits: el agente normal es de 64 bits";
        if (major < 6 || (major == 6 && minor < 1) || (major == 6 && minor == 1 && build < 7601))
            return (full, null, $"{name}: ni el agente clásico funciona (necesita Windows 7 SP1 o superior).");
        if (!hasClassic)
            return (full, null, $"{why}, y el recolector no tiene el agente clásico (actualícelo a la 0.3.8).");
        if (dotNet452 == false)
            return (full, null, $"{name}: el agente clásico necesita .NET Framework 4.5.2 o superior (instálelo con Windows Update).");
        return (full, AgentKind.Classic, null);
    }

    private static (string Name, AgentKind? Kind, string? Reason) Describe(string share, bool hasClassic)
    {
        var kernel = Path.Combine(share, "System32", "kernel32.dll");
        if (!File.Exists(kernel))
            return ("Desconocido", null, "No se pudo leer la versión de Windows del equipo.");
        var info = FileVersionInfo.GetVersionInfo(kernel);
        return DescribeVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart,
            Directory.Exists(Path.Combine(share, "SysWOW64")), hasClassic, HasDotNet452(share));
    }

    /// <summary>
    /// .NET Framework 4.5.2 o superior: su clr.dll es 4.0.30319.34209 o más (desde 4.6 la versión del archivo empieza en
    /// 4.6/4.7/4.8).
    /// </summary>
    public static bool HasDotNet452(int major, int minor, int build, int revision) =>
        major > 4 || (major == 4 && (minor > 0 || (build == 30319 && revision >= 34209) || build > 30319));

    private static bool HasDotNet452(string share)
    {
        var clr = Path.Combine(share, "Microsoft.NET", "Framework", "v4.0.30319", "clr.dll");
        if (!File.Exists(clr))
            return false;
        var v = FileVersionInfo.GetVersionInfo(clr);
        return HasDotNet452(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
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
            throw ScmFailure("No se pudo abrir el administrador de servicios del equipo");
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
            throw ScmFailure("No se pudo abrir el administrador de servicios del equipo");
        try
        {
            DeleteService(ip, TempServiceName);
            var service = Native.CreateService(manager, TempServiceName, "Centinela: instalación del agente", Native.ServiceAllAccess,
                Native.ServiceWin32OwnProcess, Native.ServiceDemandStart, Native.ServiceErrorIgnore, commandLine,
                null, IntPtr.Zero, null, null, null);
            if (service == IntPtr.Zero)
                throw ScmFailure("No se pudo crear el servicio temporal de instalación");
            try
            {
                // cmd no responde como servicio: Windows informa 1053 o 1067, pero el instalador ya quedó corriendo.
                if (!Native.StartService(service, 0, null))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is not (1053 or 1067))
                        throw new Win32Exception(error, ServiceManagerError(error, "No se pudo iniciar el instalador"));
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
        public const int ResourceTypeAny = 0;
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

        public const int LogonNewCredentials = 9;
        public const int ProviderWinNt50 = 3;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LogonUserW")]
        public static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out SafeAccessTokenHandle token);

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

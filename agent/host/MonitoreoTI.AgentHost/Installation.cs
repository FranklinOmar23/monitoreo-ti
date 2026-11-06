using System.Diagnostics;
using System.Text.Json;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Alta y baja del servicio de Windows. La llama el instalador (como administrador). Usa sc.exe e icacls.exe
/// con SID en vez de nombres de grupo: los nombres cambian según el idioma de Windows ("Administradores").
/// </summary>
public static class Installation
{
    public const string ServiceName = "MonitoreoTIAgent";
    private const string DisplayName = "MonitoreoTI Agente";
    private const string SystemSid = "*S-1-5-18";
    private const string AdministratorsSid = "*S-1-5-32-544";

    public static int Install(AgentPaths paths, CommandOptions options, TextWriter output)
    {
        // En una actualización el instalador no vuelve a pedir el servidor: se usa el de la configuración existente.
        if (string.IsNullOrWhiteSpace(options.ServerUrl))
            options = options with { ServerUrl = ReadConfig(paths).GetValueOrDefault("serverUrl") };

        if (!IsValidServerUrl(options.ServerUrl))
        {
            output.WriteLine(string.IsNullOrWhiteSpace(options.ServerUrl)
                ? "Falta la dirección del servidor."
                : $"La dirección del servidor \"{options.ServerUrl}\" no es válida: debe empezar con https:// " +
                  "(para pruebas en el mismo equipo se acepta http://localhost).");
            return 2;
        }
        if (!File.Exists(paths.ElectronExe) || !File.Exists(paths.ServiceScript))
        {
            output.WriteLine($"No se encontró el agente en {paths.AppDirectory}.");
            return 3;
        }

        // Una reinstalación primero detiene y quita el servicio anterior (los datos se conservan).
        RemoveService(output);

        // La carpeta de datos (credencial cifrada, cola local, bitácora) solo la ven SYSTEM y los administradores:
        // la credencial usa DPAPI de máquina, que cualquier proceso local podría descifrar si leyera el archivo.
        Directory.CreateDirectory(paths.DataDirectory);
        Run("icacls.exe", output, paths.DataDirectory, "/inheritance:r",
            "/grant:r", $"{SystemSid}:(OI)(CI)F", "/grant:r", $"{AdministratorsSid}:(OI)(CI)F");

        WriteConfig(paths, options);
        // Ctrl+Alt+Supr desde el escritorio remoto (SendSAS) exige esta política de Windows.
        SecureAttention.EnsurePolicy();

        var binPath = $"\"{paths.HostExe}\" run";
        if (Run("sc.exe", output, "create", ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", DisplayName) != 0)
            return 4;
        Run("sc.exe", output, "description", ServiceName, "Monitoreo de equipos de MonitoreoTI: reporta el estado de este equipo al servidor.");
        // Si el servicio cae, Windows lo levanta solo.
        Run("sc.exe", output, "failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
        Run("sc.exe", output, "start", ServiceName);

        output.WriteLine("Agente instalado y en marcha.");
        return 0;
    }

    public static int Uninstall(AgentPaths paths, CommandOptions options, TextWriter output)
    {
        RemoveService(output);

        if (!options.KeepData && Directory.Exists(paths.DataDirectory))
        {
            try
            {
                Directory.Delete(paths.DataDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"No se pudo borrar {paths.DataDirectory}: {ex.Message}");
            }
        }

        output.WriteLine("Agente desinstalado.");
        return 0;
    }

    /// <summary>
    /// config.json con la URL del servidor y el token de la sucursal. El agente borra el token del archivo
    /// en cuanto obtiene su credencial permanente. Si ya existe (reinstalación sin token), se conserva lo que había.
    /// </summary>
    private static void WriteConfig(AgentPaths paths, CommandOptions options)
    {
        var config = ReadConfig(paths);
        config["serverUrl"] = options.ServerUrl!.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(options.EnrollToken))
            config["enrollToken"] = options.EnrollToken;
        if (options.MachineType is not null)
            config["machineType"] = options.MachineType;

        File.WriteAllText(paths.ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, string> ReadConfig(AgentPaths paths)
    {
        if (!File.Exists(paths.ConfigFile))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(paths.ConfigFile)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return []; // archivo dañado: se reescribe
        }
    }

    private static void RemoveService(TextWriter output)
    {
        if (Run("sc.exe", TextWriter.Null, "query", ServiceName) != 0)
            return;

        Run("sc.exe", output, "stop", ServiceName);
        // sc.exe stop no espera: se da tiempo a que el anfitrión termine el agente antes de borrar el servicio.
        for (var i = 0; i < 30 && IsRunning(); i++)
            Thread.Sleep(500);
        Run("sc.exe", output, "delete", ServiceName);
    }

    private static bool IsRunning()
    {
        var info = new ProcessStartInfo("sc.exe", ["query", ServiceName]) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return text.Contains("STOP_PENDING", StringComparison.Ordinal) || text.Contains("RUNNING", StringComparison.Ordinal);
    }

    public static bool IsValidServerUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static int Run(string file, TextWriter output, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            output.WriteLine($"{file} {args.FirstOrDefault()}: {stdout.Trim()} {stderr.Trim()}".Trim());
        return process.ExitCode;
    }
}

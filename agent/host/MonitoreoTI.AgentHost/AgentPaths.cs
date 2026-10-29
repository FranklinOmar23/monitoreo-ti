namespace MonitoreoTI.AgentHost;

/// <summary>Opciones de la línea de comandos (--server, --token, --app, --keep-data).</summary>
public sealed record CommandOptions(string? ServerUrl, string? EnrollToken, string? AppDirectory, bool KeepData)
{
    public static CommandOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1].Trim() : null;
        }

        return new CommandOptions(
            Value("--server"),
            Value("--token"),
            Value("--app"),
            args.Any(a => string.Equals(a, "--keep-data", StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>
/// Dónde está cada cosa. El instalador deja el anfitrión en "{app}\resources\host\" y el agente en "{app}"
/// (electron.exe renombrado y el código en resources\app). Los datos van en %ProgramData%\MonitoreoTI\Agent.
/// </summary>
public sealed record AgentPaths(string AppDirectory, string ElectronExe, string ServiceScript, string DataDirectory, string HostExe)
{
    public const string ElectronFileName = "MonitoreoTI Agente.exe";

    public string ConfigFile => Path.Combine(DataDirectory, "config.json");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static AgentPaths Resolve(string? appDirectory)
    {
        var host = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "MonitoreoTI.AgentHost.exe");
        var app = appDirectory ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(host)!, "..", ".."));
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        // Solo para desarrollo: otra carpeta de datos sin tocar la del servicio instalado.
        var dataOverride = Environment.GetEnvironmentVariable("MONITOREOTI_DATA_DIR");

        return new AgentPaths(
            app,
            Path.Combine(app, ElectronFileName),
            Path.Combine(app, "resources", "app", "out", "main", "service.js"),
            string.IsNullOrWhiteSpace(dataOverride) ? Path.Combine(programData, "MonitoreoTI", "Agent") : dataOverride,
            host);
    }
}

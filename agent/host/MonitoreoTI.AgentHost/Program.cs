using MonitoreoTI.AgentHost;

// Uso:
//   MonitoreoTI.AgentHost.exe run                                   (lo ejecuta Windows como servicio; en consola sirve para depurar)
//   MonitoreoTI.AgentHost.exe install --server <url> --token <tok>  (lo llama el instalador, como administrador)
//   MonitoreoTI.AgentHost.exe uninstall [--keep-data]               (lo llama el desinstalador)

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
var options = CommandOptions.Parse(args.Skip(1).ToArray());
var paths = AgentPaths.Resolve(options.AppDirectory);

switch (command)
{
    case "install":
        return Installation.Install(paths, options, Console.Out);
    case "uninstall":
        return Installation.Uninstall(paths, options, Console.Out);
    case "run":
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = Installation.ServiceName);
        builder.Services.AddSingleton(paths);
        builder.Services.AddHostedService<AgentSupervisor>();
        await builder.Build().RunAsync();
        return 0;
    default:
        Console.Error.WriteLine($"Comando desconocido: {command}. Use run, install o uninstall.");
        return 2;
}

using MonitoreoTI.AgentHost;

// Uso:
//   MonitoreoTI.AgentHost.exe run                                   (lo ejecuta Windows como servicio; en consola sirve para depurar)
//   MonitoreoTI.AgentHost.exe install --server <url> --token <tok>  (lo llama el instalador, como administrador)
//   MonitoreoTI.AgentHost.exe uninstall [--keep-data]               (lo llama el desinstalador)
//   MonitoreoTI.AgentHost.exe snmp                                  (lo llama el agente recolector: petición JSON por stdin)
//   MonitoreoTI.AgentHost.exe ping                                  (barrido de IP del recolector: petición JSON por stdin)
//   MonitoreoTI.AgentHost.exe instalar-remoto                       (instala el agente en otro equipo por SMB; ver RemoteInstaller)
//   MonitoreoTI.AgentHost.exe pty --shell powershell|cmd --cols N --rows N   (terminal remota; ver PtyHost)
//   MonitoreoTI.AgentHost.exe escritorio                            (escritorio remoto: abre el asistente en la sesión del usuario)
//   MonitoreoTI.AgentHost.exe asistente                             (asistente de sesión; lo abre "escritorio")
//   MonitoreoTI.AgentHost.exe prueba-pantalla [--archivo x.h264]    (soporte: ¿este equipo puede usar la pantalla V2?)

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "run";
var options = CommandOptions.Parse(args.Skip(1).ToArray());
var paths = AgentPaths.Resolve(options.AppDirectory);

switch (command)
{
    case "install":
        return Installation.Install(paths, options, Console.Out);
    case "uninstall":
        return Installation.Uninstall(paths, options, Console.Out);
    case "snmp":
        return await SnmpQuery.RunAsync(Console.In, Console.Out);
    case "ping":
        return await IpSweep.RunAsync(Console.In, Console.Out);
    case "instalar-remoto":
        return await RemoteInstaller.RunAsync(Console.In, Console.Out);
    case "pty":
        return await PtyHost.RunAsync(args);
    case "escritorio":
        return await SessionLauncher.RunAsync(paths);
    case "asistente":
        return await DesktopHelper.RunAsync();
    case "prueba-pantalla":
        return ScreenDiagnostics.Run(args.SkipWhile(a => a != "--archivo").Skip(1).FirstOrDefault(), Console.Out);
    case "run":
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = Installation.ServiceName);
        builder.Services.AddSingleton(paths);
        builder.Services.AddHostedService<AgentSupervisor>();
        await builder.Build().RunAsync();
        return 0;
    default:
        Console.Error.WriteLine($"Comando desconocido: {command}. Use run, install, uninstall, snmp, ping, instalar-remoto, pty, escritorio, asistente o prueba-pantalla.");
        return 2;
}

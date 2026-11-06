using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    // Uso:
    //   MonitoreoTI.AgenteClasico.exe instalar /SERVER=https://servidor:5127 /TOKEN=<token> [/TIPO=servidor]   (como administrador o SYSTEM)
    //   MonitoreoTI.AgenteClasico.exe instalar        (sin token: actualiza el programa de un equipo ya registrado)
    //   MonitoreoTI.AgenteClasico.exe desinstalar [/CONSERVAR]
    //   MonitoreoTI.AgenteClasico.exe consola        (corre en primer plano, para revisar)
    //   MonitoreoTI.AgenteClasico.exe diagnostico    (muestra lo que reportaría, sin conectarse)
    //   MonitoreoTI.AgenteClasico.exe servicio       (lo usa Windows)
    public static class Program
    {
        public static int Main(string[] args)
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "servicio";
            try
            {
                switch (command)
                {
                    case "instalar":
                        return Setup.Install(Option(args, "SERVER"), Option(args, "TOKEN"), Option(args, "TIPO"));
                    case "desinstalar":
                        return Setup.Uninstall(args.Any(a => a.Equals("/CONSERVAR", StringComparison.OrdinalIgnoreCase)));
                    case "consola":
                        using (var agent = new Agent())
                        {
                            agent.Start();
                            Console.WriteLine("Agente clásico en marcha. Enter para salir.");
                            Console.ReadLine();
                        }
                        return 0;
                    case "diagnostico":
                        // Lo que reportaría este equipo, sin conectarse (soporte).
                        Diagnostics.Print();
                        return 0;
                    case "servicio":
                        ServiceBase.Run(new AgentService());
                        return 0;
                    case "asistente":
                        // Lo lanza el servicio en la sesión del usuario para el escritorio remoto (decisión 84).
                        return Assistant.Run();
                    default:
                        Console.Error.WriteLine("Comandos: instalar /SERVER= /TOKEN= [/TIPO=servidor], desinstalar [/CONSERVAR], consola, diagnostico.");
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{command}: {ex.Message}");
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        /// <summary>"/SERVER=x" o "--server x".</summary>
        public static string Option(string[] args, string name)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("/" + name + "=", StringComparison.OrdinalIgnoreCase))
                    return args[i].Substring(name.Length + 2);
                if (args[i].Equals("--" + name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    return args[i + 1];
            }
            return null;
        }
    }

    public static class Diagnostics
    {
        public static void Print()
        {
            var identity = Identity.Current();
            Console.WriteLine($"Agente clásico {AgentInfo.Version} en {identity.Hostname} ({identity.OperatingSystem}), MAC {identity.MacAddress}, IP {identity.IpAddress}, tipo {identity.MachineType}");
            var parts = new Dictionary<string, Func<Dictionary<string, object>>>
            {
                ["metricas"] = Collectors.Metrics,
                ["inventario_hardware"] = Collectors.Hardware,
                ["interfaces_red"] = Collectors.Interfaces,
                ["volumenes"] = Collectors.Volumes,
                ["procesos"] = Collectors.Processes,
                ["servicios"] = Collectors.Services,
                ["salud_discos"] = Collectors.DiskHealth,
                ["seguridad"] = Collectors.Security,
            };
            foreach (var part in parts)
            {
                var watch = Stopwatch.StartNew();
                var json = Json.Write(part.Value());
                Console.WriteLine($"--- {part.Key} ({watch.ElapsedMilliseconds} ms, {json.Length} bytes)");
                Console.WriteLine(json.Length > 1500 ? json.Substring(0, 1500) + "…" : json);
            }
            var software = (List<object>)Collectors.Software()["programas"];
            Console.WriteLine($"--- inventario_software: {software.Count} programas");
        }
    }

    public sealed class AgentService : ServiceBase
    {
        private Agent _agent;

        public AgentService()
        {
            ServiceName = Paths.ServiceName;
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            _agent = new Agent();
            _agent.Start();
        }

        protected override void OnStop() => _agent?.Dispose();

        protected override void OnShutdown() => _agent?.Dispose();
    }

    /// <summary>El agente: conexión y reportes con los mismos tiempos que el agente normal.</summary>
    public sealed class Agent : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly List<Timer> _timers = new List<Timer>();
        private Timer _metrics;
        private Gateway _gateway;
        private Commands _commands;
        private RemoteSessions _sessions;

        public void Start()
        {
            Tls.Configure();
            var config = AgentConfig.Load() ?? throw new InvalidOperationException("El agente no está registrado: ejecute 'instalar /SERVER= /TOKEN='.");
            Log.Info($"Agente clásico {AgentInfo.Version} (equipo {config.MachineId}) iniciando.");
            _gateway = new Gateway(config, OnWelcome, OnMessage);
            _commands = new Commands(config, (tipo, datos) => _gateway.Enqueue(tipo, datos));
            _sessions = new RemoteSessions(config, (tipo, datos) => _gateway.Enqueue(tipo, datos));

            Every("inventario_hardware", Collectors.Hardware, TimeSpan.FromSeconds(5), TimeSpan.FromHours(24));
            Every("inventario_software", Collectors.Software, TimeSpan.FromSeconds(15), TimeSpan.FromHours(24));
            Every("interfaces_red", Collectors.Interfaces, TimeSpan.FromSeconds(25), TimeSpan.FromHours(6));
            Every("eventos", Collectors.Events, TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
            Every("volumenes", Collectors.Volumes, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
            Every("procesos", Collectors.Processes, TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(5));
            Every("servicios", Collectors.Services, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(5));
            Every("salud_discos", Collectors.DiskHealth, TimeSpan.FromSeconds(60), TimeSpan.FromHours(6));
            Every("seguridad", Collectors.Security, TimeSpan.FromSeconds(70), TimeSpan.FromHours(6));
            _metrics = Every("metricas", Collectors.Metrics, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            Task.Run(() => _gateway.RunAsync(_stop.Token));
        }

        private Timer Every(string tipo, Func<Dictionary<string, object>> collect, TimeSpan first, TimeSpan every)
        {
            var running = 0;
            var timer = new Timer(_ =>
            {
                // Una consulta lenta no se superpone con la siguiente.
                if (Interlocked.Exchange(ref running, 1) == 1)
                    return;
                try
                {
                    _gateway.Enqueue(tipo, collect());
                }
                catch (Exception ex)
                {
                    Log.Error($"No se pudo recolectar {tipo}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref running, 0);
                }
            }, null, first, every);
            _timers.Add(timer);
            return timer;
        }

        /// <summary>El servidor fija cada cuánto van las métricas.</summary>
        private void OnWelcome(int metricsSeconds) =>
            _metrics?.Change(TimeSpan.FromSeconds(metricsSeconds), TimeSpan.FromSeconds(metricsSeconds));

        private void OnMessage(Dictionary<string, object> message)
        {
            switch (message.Str("tipo"))
            {
                case "ejecutar_comando":
                    Task.Run(() => _commands.HandleAsync(message.Obj("datos")));
                    break;
                case "solicitar_inventario":
                    Task.Run(() =>
                    {
                        _gateway.Enqueue("inventario_hardware", Collectors.Hardware());
                        _gateway.Enqueue("inventario_software", Collectors.Software());
                    });
                    break;
                case "abrir_sesion_remota":
                {
                    var datos = message.Obj("datos");
                    if (datos != null)
                        _sessions.Open(datos);
                    break;
                }
                case "cerrar_sesion_remota":
                {
                    var id = message.Obj("datos")?.Int("sesion_id");
                    if (id != null)
                        _sessions.Close(id.Value);
                    break;
                }
                default:
                    // Recolector, respaldos y actualizaciones: no existen en el agente clásico.
                    break;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _sessions?.Dispose();
            foreach (var t in _timers)
                t.Dispose();
            Log.Info("Agente clásico detenido.");
        }
    }

    /// <summary>
    /// Instalación: se copia a %ProgramFiles%\MonitoreoTI\AgenteClasico con el certificado de Centinela que tenga al lado,
    /// registra el equipo con el token (si falla, no queda nada instalado) y crea el servicio MonitoreoTIAgent.
    /// </summary>
    public static class Setup
    {
        public static int Install(string server, string token, string type = null)
        {
            // Sin token y ya registrado: solo se actualiza el programa (instalador único y reinstalación remota, decisión 84).
            var update = string.IsNullOrWhiteSpace(token) && AgentConfig.Load() != null;
            if (!update && (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(token)))
                throw new ArgumentException("Indique /SERVER=https://servidor:puerto y /TOKEN=<token de la sucursal>.");
            if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                throw new UnauthorizedAccessException("Ejecute la instalación como administrador.");
            Directory.CreateDirectory(Paths.DataDirectory);
            Log.Info(update ? $"Actualizando el agente clásico a {AgentInfo.Version}." : $"Instalando el agente clásico (servidor {server}).");

            var source = Assembly.GetExecutingAssembly().Location;
            var sourceDir = Path.GetDirectoryName(source);
            var ca = Path.Combine(sourceDir, Paths.CaFileName);
            if (File.Exists(ca))
                File.Copy(ca, Paths.TrustedCaFile, true);
            Tls.Configure();

            if (ServiceExists())
                StopService();
            if (!update)
            {
                var identity = Identity.Current();
                if (string.Equals(type, "servidor", StringComparison.OrdinalIgnoreCase))
                    identity.MachineType = "servidor";
                var config = Enrollment.Enroll(server.Trim(), token.Trim(), identity);
                config.Save();
                Log.Info($"Equipo registrado (máquina {config.MachineId}).");
            }

            Directory.CreateDirectory(Paths.ProgramDirectory);
            var target = Path.Combine(Paths.ProgramDirectory, Paths.ExeName);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(1000);
                File.Copy(source, target, true);
            }

            if (!ServiceExists())
            {
                if (Run("sc.exe", "create", Paths.ServiceName, "binPath=", $"\"{target}\" servicio", "start=", "auto",
                        "DisplayName=", "MonitoreoTI Agente (clásico)") != 0)
                    throw new InvalidOperationException("No se pudo crear el servicio de Windows.");
                Run("sc.exe", "description", Paths.ServiceName, "Monitoreo de equipos de MonitoreoTI (agente clásico para Windows 7).");
                Run("sc.exe", "failure", Paths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
            }
            Run("sc.exe", "start", Paths.ServiceName);
            Log.Info("Agente clásico instalado y en marcha.");
            return 0;
        }

        public static int Uninstall(bool keepData)
        {
            Run("sc.exe", "stop", Paths.ServiceName);
            Thread.Sleep(2000);
            Run("sc.exe", "delete", Paths.ServiceName);
            if (!keepData && File.Exists(Paths.ConfigFile))
                File.Delete(Paths.ConfigFile);
            Log.Info("Agente clásico desinstalado.");
            return 0;
        }

        /// <summary>Detiene el servicio y espera a que suelte el programa (para poder reemplazarlo).</summary>
        private static void StopService()
        {
            Run("sc.exe", "stop", Paths.ServiceName);
            try
            {
                using (var service = new ServiceController(Paths.ServiceName))
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            catch (Exception)
            {
                // Si no se detuvo a tiempo, la copia lo dirá.
            }
        }

        private static bool ServiceExists() => ServiceController.GetServices().Any(s => s.ServiceName == Paths.ServiceName);

        private static int Run(string exe, params string[] args)
        {
            using (var process = Process.Start(new ProcessStartInfo(exe, CommandLine(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }))
            {
                process.StandardOutput.ReadToEnd();
                process.WaitForExit(60000);
                return process.ExitCode;
            }
        }

        /// <summary>
        /// Arma la línea de comandos para que cada argumento llegue entero: el binPath de sc es "\"ruta con espacios\" servicio"
        /// (con sus comillas internas), no la ruta suelta seguida de "servicio".
        /// </summary>
        public static string CommandLine(params string[] args) => string.Join(" ", args.Select(Quote));

        private static string Quote(string arg) =>
            arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
    }
}

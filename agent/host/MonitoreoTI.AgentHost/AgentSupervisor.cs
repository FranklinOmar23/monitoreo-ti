using System.Diagnostics;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Lanza el agente (Electron en modo Node) y lo vuelve a lanzar si termina, con espera creciente para no
/// entrar en un bucle si falla al arrancar. Lo que escribe el agente queda en logs\agente.log (rotativo).
/// </summary>
public sealed class AgentSupervisor(AgentPaths paths, ILogger<AgentSupervisor> logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var log = new RotatingLog(paths.LogDirectory, "agente.log");
        var backoff = TimeSpan.FromSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            int exitCode;
            try
            {
                exitCode = await RunOnceAsync(log, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Write($"No se pudo iniciar el agente: {ex.Message}");
                logger.LogError(ex, "No se pudo iniciar el agente.");
                exitCode = -1;
            }

            if (stoppingToken.IsCancellationRequested)
                return;

            // Si funcionó un buen rato antes de caer, se reintenta rápido; si cae al arrancar, se espera cada vez más.
            backoff = DateTime.UtcNow - started > HealthyRun ? TimeSpan.FromSeconds(5) : Min(backoff * 2, MaxBackoff);
            log.Write($"El agente terminó (código {exitCode}); se reinicia en {backoff.TotalSeconds:0} s.");
            await Task.Delay(backoff, stoppingToken);
        }
    }

    private async Task<int> RunOnceAsync(RotatingLog log, CancellationToken stoppingToken)
    {
        var start = new ProcessStartInfo(paths.ElectronExe)
        {
            WorkingDirectory = paths.AppDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(paths.ServiceScript);
        start.Environment["ELECTRON_RUN_AS_NODE"] = "1";
        start.Environment["MONITOREOTI_DATA_DIR"] = paths.DataDirectory;

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log.Write(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.Write(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        log.Write($"Agente iniciado (PID {process.Id}).");

        try
        {
            await process.WaitForExitAsync(stoppingToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Windows pidió detener el servicio: el agente no tiene ventana a la que avisar, se termina.
            // Su cola local (SQLite) es transaccional: no queda a medias.
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            log.Write("Servicio detenido.");
            throw;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>Bitácora en texto que rota al llegar a 5 MB (se guardan 3 archivos).</summary>
public sealed class RotatingLog : IDisposable
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int Keep = 3;

    private readonly string _path;
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public RotatingLog(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, fileName);
    }

    public void Write(string line)
    {
        lock (_lock)
        {
            if (_writer is null || _writer.BaseStream.Length > MaxBytes)
                Rotate();
            _writer!.WriteLine(line.StartsWith('{') ? line : $"{DateTime.UtcNow:O} {line}");
            _writer.Flush();
        }
    }

    private void Rotate()
    {
        if (_writer is not null)
        {
            _writer.Dispose();
            for (var i = Keep - 1; i >= 1; i--)
            {
                var from = i == 1 ? _path : $"{_path}.{i - 1}";
                if (File.Exists(from))
                    File.Move(from, $"{_path}.{i}", overwrite: true);
            }
        }
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read));
    }

    public void Dispose()
    {
        lock (_lock)
            _writer?.Dispose();
    }
}

using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;

namespace MonitoreoTI.AgentHost.Backup;

/// <summary>
/// Lo que el panel le pregunta al servidor para configurar un respaldo (decisión 82):
/// "respaldo-explorar" (instancias y bases de SQL Server, servicios y discos externos), "respaldo-discos" (solo los
/// discos, para el aviso de disco desconectado) y "respaldo-probar" (abrir el destino y escribir un archivo de prueba).
/// Cada uno escribe una sola línea JSON.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BackupTools
{
    public static async Task<int> ExploreAsync(TextWriter output)
    {
        var instances = new List<object>();
        foreach (var name in SqlBackup.Instances())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var described = await SqlBackup.DescribeAsync(name, timeout.Token);
                instances.Add(new
                {
                    nombre = described.Name,
                    error = described.Error,
                    bases = described.Databases.Select(d => new { nombre = d.Name, tamano_mb = d.SizeMb, recuperacion = d.RecoveryModel }),
                });
            }
            catch (OperationCanceledException)
            {
                instances.Add(new { nombre = name, error = "SQL Server no respondió a tiempo.", bases = Array.Empty<object>() });
            }
        }

        var services = ServiceController.GetServices()
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                using (s)
                    return new { nombre = s.ServiceName, mostrar = s.DisplayName, estado = s.Status == ServiceControllerStatus.Running ? "en_ejecucion" : "detenido" };
            })
            .ToList();

        object disks;
        string? diskError = null;
        try
        {
            disks = Disks();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException)
        {
            disks = Array.Empty<object>();
            diskError = $"No se pudieron leer los discos: {ex.Message}";
        }
        Write(output, new { ok = true, error = diskError, instancias = instances, servicios = services, discos = disks });
        return 0;
    }

    public static int ListDisks(TextWriter output)
    {
        try
        {
            Write(output, new { ok = true, discos = Disks() });
            return 0;
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException)
        {
            Write(output, new { ok = false, error = ex.Message, discos = Array.Empty<object>() });
            return 1;
        }
    }

    private static object Disks() => BackupDestinations.ListExternalDisks()
        .Select(d => new { serie = d.Serial, nombre = d.Name, letra = d.Letter, total_gb = d.TotalGb, libre_gb = d.FreeGb })
        .ToList();

    public static async Task<int> TestAsync(TextReader input, TextWriter output)
    {
        BackupDestination? destination;
        try
        {
            destination = JsonSerializer.Deserialize<BackupDestination>(await input.ReadToEndAsync(), BackupJson.Options);
        }
        catch (JsonException)
        {
            destination = null;
        }
        if (destination is null)
        {
            Write(output, new { ok = false, error = "Pedido inválido." });
            return 2;
        }
        try
        {
            using var open = BackupDestinations.Open(destination);
            BackupDestinations.ProbeWrite(open.Root);
            Write(output, new { ok = true, libre_gb = BackupDestinations.FreeGb(open.Root), ubicacion = open.Description });
            return 0;
        }
        catch (Exception ex) when (ex is DestinationException or IOException or UnauthorizedAccessException or System.Management.ManagementException)
        {
            var message = ex is UnauthorizedAccessException ? $"No se puede escribir en el destino: {ex.Message}" : ex.Message;
            Write(output, new { ok = false, error = message });
            return 1;
        }
    }

    private static void Write(TextWriter output, object value)
    {
        output.WriteLine(JsonSerializer.Serialize(value));
        output.Flush();
    }
}

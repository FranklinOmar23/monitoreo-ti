using System.Management;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace MonitoreoTI.AgentHost.Backup;

/// <summary>
/// Instantáneas de Windows (VSS) por WMI: se copia desde la foto del volumen, así salen bien los archivos abiertos y,
/// en frío, los servicios vuelven a arrancar apenas se toma la foto.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShadowCopies : IDisposable
{
    private readonly Dictionary<string, (string Id, string Device)> _byVolume = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Toma la instantánea del volumen (C:\); devuelve el error si Windows no la pudo hacer.</summary>
    public string? Create(string volume)
    {
        if (_byVolume.ContainsKey(volume))
            return null;
        try
        {
            using var shadowClass = new ManagementClass(@"\\.\root\cimv2", "Win32_ShadowCopy", null);
            using var input = shadowClass.GetMethodParameters("Create");
            input["Volume"] = volume;
            input["Context"] = "ClientAccessible";
            using var output = shadowClass.InvokeMethod("Create", input, null);
            var code = Convert.ToUInt32(output["ReturnValue"]);
            if (code != 0)
                return $"Windows no pudo hacer la instantánea de {volume} (código {code}).";
            var id = (string)output["ShadowID"];
            using var searcher = new ManagementObjectSearcher($"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{id}'");
            foreach (ManagementObject shadow in searcher.Get())
            {
                using (shadow)
                {
                    _byVolume[volume] = (id, (string)shadow["DeviceObject"]);
                    return null;
                }
            }
            return $"No se encontró la instantánea de {volume}.";
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or InvalidCastException)
        {
            return $"Windows no pudo hacer la instantánea de {volume}: {ex.Message}";
        }
    }

    /// <summary>D:\Datos\a.txt → \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\Datos\a.txt (o la ruta tal cual sin instantánea).</summary>
    public string Map(string path)
    {
        var volume = Path.GetPathRoot(path) ?? "";
        return _byVolume.TryGetValue(volume, out var shadow) ? shadow.Device + "\\" + path[volume.Length..] : path;
    }

    public bool Has(string volume) => _byVolume.ContainsKey(volume);

    public void Dispose()
    {
        foreach (var (id, _) in _byVolume.Values)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_ShadowCopy WHERE ID = '{id}'");
                foreach (ManagementObject shadow in searcher.Get())
                {
                    using (shadow)
                        shadow.Delete();
                }
            }
            catch (ManagementException)
            {
                // Si no se pudo borrar, Windows la descarta sola cuando necesita el espacio.
            }
        }
        _byVolume.Clear();
    }
}

/// <summary>
/// Respaldo en frío: detiene los servicios elegidos (y los que dependen de ellos) y los vuelve a arrancar siempre, en el
/// orden inverso, aunque el respaldo falle. Devuelve los que no volvieron a arrancar.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ColdServices
{
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(3);
    private readonly List<string> _stopped = [];
    private bool _restarted;

    public IReadOnlyList<string> Stopped => _stopped;

    public void Stop(IEnumerable<string> names, Action<string> progress)
    {
        foreach (var name in names)
        {
            using var service = new ServiceController(name);
            ServiceControllerStatus status;
            try
            {
                status = service.Status;
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException($"No existe el servicio {name} en este servidor.");
            }
            if (status == ServiceControllerStatus.Stopped)
                continue;
            // Primero los que dependen de él (Windows no deja detenerlo con ellos en marcha).
            foreach (var dependent in service.DependentServices.Where(d => d.Status != ServiceControllerStatus.Stopped))
            {
                progress($"Deteniendo {dependent.DisplayName}…");
                dependent.Stop();
                dependent.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
                _stopped.Add(dependent.ServiceName);
            }
            progress($"Deteniendo {service.DisplayName}…");
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
            _stopped.Add(service.ServiceName);
        }
    }

    /// <summary>Arranca lo detenido (una sola vez). Lo que no llega a "en ejecución" se informa.</summary>
    public List<string> Restart(Action<string> progress)
    {
        if (_restarted)
            return [];
        _restarted = true;
        var failed = new List<string>();
        foreach (var name in Enumerable.Reverse(_stopped).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status == ServiceControllerStatus.Running)
                    continue;
                progress($"Iniciando {service.DisplayName}…");
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, Wait);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
            {
                failed.Add(name);
            }
        }
        return failed;
    }
}

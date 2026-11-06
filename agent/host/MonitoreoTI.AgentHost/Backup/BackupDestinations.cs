using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MonitoreoTI.AgentHost.Backup;

/// <summary>Un disco externo conectado: la serie lo identifica aunque cambie la letra.</summary>
public sealed record ExternalDisk(string Serial, string Name, string? Letter, double? TotalGb, double? FreeGb, string? PnpDeviceId);

/// <summary>El destino listo para escribir; al cerrarlo se suelta la conexión de red.</summary>
public sealed class OpenDestination(string root, string description, ExternalDisk? disk, Action? release) : IDisposable
{
    public string Root { get; } = root;
    public string Description { get; } = description;
    public ExternalDisk? Disk { get; } = disk;

    public void Dispose() => release?.Invoke();
}

public sealed class DestinationException(string message) : Exception(message);

/// <summary>
/// Destinos de los respaldos (decisión 82): disco externo (se busca por su número de serie entre los conectados), carpeta
/// de red (con su propia cuenta, que solo vive en memoria) o carpeta local.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BackupDestinations
{
    /// <summary>Discos USB o externos conectados, con su letra y espacio.</summary>
    public static List<ExternalDisk> ListExternalDisks()
    {
        var result = new List<ExternalDisk>();
        using var drives = new ManagementObjectSearcher("SELECT DeviceID, Model, SerialNumber, InterfaceType, MediaType, PNPDeviceID FROM Win32_DiskDrive");
        foreach (ManagementObject drive in drives.Get())
        {
            using (drive)
            {
                var iface = drive["InterfaceType"] as string ?? "";
                var media = drive["MediaType"] as string ?? "";
                var pnp = drive["PNPDeviceID"] as string ?? "";
                var external = iface.Equals("USB", StringComparison.OrdinalIgnoreCase)
                    || media.Contains("External", StringComparison.OrdinalIgnoreCase)
                    || media.Contains("Removable", StringComparison.OrdinalIgnoreCase)
                    || pnp.StartsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)
                    || pnp.StartsWith("SCSI\\DISK&VEN_", StringComparison.OrdinalIgnoreCase) && pnp.Contains("UAS", StringComparison.OrdinalIgnoreCase);
                if (!external)
                    continue;

                var model = (drive["Model"] as string)?.Trim() ?? "Disco externo";
                string? letter = null;
                string? volumeSerial = null;
                double? total = null, free = null;
                foreach (ManagementObject partition in drive.GetRelated("Win32_DiskPartition"))
                {
                    using (partition)
                    {
                        foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                        {
                            using (logical)
                            {
                                letter ??= logical["DeviceID"] as string;
                                volumeSerial ??= logical["VolumeSerialNumber"] as string;
                                if (total is null && logical["Size"] is ulong size)
                                    total = Math.Round(size / 1073741824.0, 1);
                                if (free is null && logical["FreeSpace"] is ulong freeSpace)
                                    free = Math.Round(freeSpace / 1073741824.0, 1);
                            }
                        }
                    }
                }
                // Algunos adaptadores USB no dan la serie del disco: entonces se usa la del volumen.
                var serial = (drive["SerialNumber"] as string)?.Trim();
                if (string.IsNullOrEmpty(serial))
                    serial = volumeSerial is null ? null : "VOL-" + volumeSerial;
                if (serial is null)
                    continue;
                result.Add(new ExternalDisk(serial, model, letter, total, free, pnp));
            }
        }
        return result;
    }

    /// <summary>
    /// Abre el destino del trabajo: busca el disco, conecta la carpeta de red o crea la local, y deja lista la carpeta.
    /// </summary>
    public static OpenDestination Open(BackupDestination destination)
    {
        switch (destination.Tipo)
        {
            case "disco_externo":
            {
                var connected = ListExternalDisks();
                var disk = destination.Discos
                    .Select(d => connected.FirstOrDefault(c => c.Serial.Equals(d.Serie.Trim(), StringComparison.OrdinalIgnoreCase) && c.Letter is not null))
                    .FirstOrDefault(c => c is not null);
                if (disk is null)
                    throw new DestinationException(
                        $"No está conectado ninguno de los discos del respaldo ({string.Join(", ", destination.Discos.Select(d => d.Nombre))}).");
                var name = destination.Discos.First(d => d.Serie.Trim().Equals(disk.Serial, StringComparison.OrdinalIgnoreCase)).Nombre;
                var root = Path.Combine(disk.Letter! + "\\", string.IsNullOrWhiteSpace(destination.Ruta) ? "Centinela" : destination.Ruta);
                Directory.CreateDirectory(root);
                return new OpenDestination(root, $"Disco {name} ({disk.Letter})", disk, null);
            }
            case "red":
            {
                var share = ShareRoot(destination.Ruta);
                Action? release = null;
                if (!string.IsNullOrEmpty(destination.Usuario))
                {
                    Native.WNetCancelConnection2(share, 0, true);
                    var resource = new Native.NetResource { Type = 1, RemoteName = share };
                    var code = Native.WNetAddConnection2(ref resource, destination.Clave ?? "", destination.Usuario, 0);
                    if (code != 0)
                        throw new DestinationException(NetworkError(code, destination.Ruta));
                    release = () => Native.WNetCancelConnection2(share, 0, true);
                }
                try
                {
                    Directory.CreateDirectory(destination.Ruta);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    release?.Invoke();
                    throw new DestinationException($"No se pudo usar {destination.Ruta}: {ex.Message}");
                }
                return new OpenDestination(destination.Ruta, destination.Ruta, null, release);
            }
            case "local":
                try
                {
                    Directory.CreateDirectory(destination.Ruta);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DestinationException($"No se pudo usar {destination.Ruta}: {ex.Message}");
                }
                return new OpenDestination(destination.Ruta, destination.Ruta, null, null);
            default:
                throw new DestinationException("Destino no válido.");
        }
    }

    /// <summary>\\servidor\recurso\sub\carpeta → \\servidor\recurso.</summary>
    public static string ShareRoot(string unc)
    {
        var parts = unc.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : unc;
    }

    public static string NetworkError(int code, string path) => code switch
    {
        1326 => $"Usuario o clave incorrectos para {path}.",
        5 => $"La cuenta no tiene acceso a {path}.",
        53 or 67 or 64 => $"No se encontró {path}: revise el nombre del servidor y del recurso compartido, y que esté encendido.",
        1219 => $"Este servidor ya está conectado a {ShareRoot(path)} con otra cuenta (por ejemplo, una unidad de red de otro usuario).",
        1909 => "La cuenta está bloqueada.",
        1331 => "La cuenta está deshabilitada.",
        1907 or 1330 => "La clave de la cuenta venció.",
        _ => $"No se pudo conectar a {path}: {new Win32Exception(code).Message} ({code}).",
    };

    /// <summary>Espacio libre en GB de la carpeta (local, disco o red).</summary>
    public static double? FreeGb(string folder) =>
        Native.GetDiskFreeSpaceEx(folder, out var free, out _, out _) ? Math.Round(free / 1073741824.0, 1) : null;

    /// <summary>Escribe y borra un archivo de prueba: confirma que se puede guardar ahí.</summary>
    public static void ProbeWrite(string folder)
    {
        var probe = Path.Combine(folder, $".centinela-prueba-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "Centinela");
        File.Delete(probe);
    }

    /// <summary>
    /// Expulsa el disco con seguridad (como "Quitar hardware de forma segura"). Si Windows no lo deja (algo lo tiene
    /// abierto), devuelve el motivo.
    /// </summary>
    public static string? Eject(ExternalDisk disk)
    {
        if (string.IsNullOrEmpty(disk.PnpDeviceId))
            return "No se pudo identificar el disco para expulsarlo.";
        if (Native.CM_Locate_DevNodeW(out var node, disk.PnpDeviceId, 0) != 0)
            return "No se encontró el disco para expulsarlo.";
        // Se expulsa el dispositivo USB que contiene el disco; si no tiene, el disco mismo.
        var target = Native.CM_Get_Parent(out var parent, node, 0) == 0 ? parent : node;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var vetoName = new char[260];
            if (Native.CM_Request_Device_EjectW(target, out var veto, vetoName, vetoName.Length, 0) == 0 && veto == 0)
                return null;
            Thread.Sleep(2000);
        }
        return "Windows no dejó expulsar el disco (algún programa lo tiene abierto); quítelo con «Quitar hardware de forma segura».";
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NetResource
        {
            public int Scope;
            public int Type;
            public int DisplayType;
            public int Usage;
            public string? LocalName;
            public string? RemoteName;
            public string? Comment;
            public string? Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
        public static extern int WNetAddConnection2(ref NetResource resource, string password, string username, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
        public static extern int WNetCancelConnection2(string name, int flags, bool force);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        public static extern int CM_Get_Parent(out uint parent, uint devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, char[] vetoName, int nameLength, int flags);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>Consultas WMI (funcionan igual en Windows 7 y 10, sin PowerShell 3).</summary>
    public static class Wmi
    {
        public static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, wql))
                using (var results = searcher.Get())
                    return results.Cast<ManagementBaseObject>().ToList();
            }
            catch (Exception ex) when (ex is ManagementException || ex is UnauthorizedAccessException || ex is System.Runtime.InteropServices.COMException)
            {
                return new List<ManagementBaseObject>();
            }
        }

        public static string S(ManagementBaseObject o, string p)
        {
            try
            {
                var text = o[p]?.ToString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (ManagementException)
            {
                return null;
            }
        }

        public static double? D(ManagementBaseObject o, string p)
        {
            try
            {
                var v = o[p];
                return v == null ? (double?)null : Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is ManagementException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return null;
            }
        }

        public sealed class OsInfo
        {
            public string Name;
            public bool IsServer;
        }

        private static OsInfo _os;

        public static OsInfo OperatingSystem()
        {
            if (_os != null)
                return _os;
            var os = Query("SELECT Caption, Version, CSDVersion FROM Win32_OperatingSystem").FirstOrDefault();
            var caption = os == null ? "Windows" : S(os, "Caption") ?? "Windows";
            var name = string.Join(" ", new[] { caption, S(os, "CSDVersion"), S(os, "Version") }.Where(s => s != null));
            return _os = new OsInfo { Name = name, IsServer = caption.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0 };
        }

        public static bool HasBattery() => Query("SELECT Name FROM Win32_Battery").Count > 0;
    }

    /// <summary>Lo mismo que reporta el agente normal, con el mismo formato de mensajes.</summary>
    public static class Collectors
    {
        private const double Gib = 1024.0 * 1024 * 1024;
        private static readonly Regex Wifi = new Regex(@"wi-?fi|wireless|802\.11|wlan", RegexOptions.IgnoreCase);
        private static readonly Regex Virtual = new Regex(@"virtual|vpn|tap-|vmware|hyper-v|loopback|bluetooth|miniport|wan |wsl", RegexOptions.IgnoreCase);

        private static double Round1(double v) => Math.Round(Math.Min(100, Math.Max(0, v)) * 10) / 10;
        private static double Gb(double? bytes) => Math.Round((bytes ?? 0) / Gib * 10) / 10;

        // ------------------------------------------------------------------ métricas

        public static Dictionary<string, object> Metrics()
        {
            var cpu = Wmi.Query("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'").FirstOrDefault();
            var os = Wmi.Query("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem").FirstOrDefault();
            double? ram = null;
            if (os != null && Wmi.D(os, "TotalVisibleMemorySize") is double total && total > 0)
                ram = Round1(100 * (total - (Wmi.D(os, "FreePhysicalMemory") ?? 0)) / total);
            double? disk = null;
            foreach (var d in Wmi.Query("SELECT Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
                if (Wmi.D(d, "Size") is double size && size > 0)
                    disk = Math.Max(disk ?? 0, Round1(100 * (size - (Wmi.D(d, "FreeSpace") ?? 0)) / size));
            return new Dictionary<string, object>
            {
                ["cpu"] = cpu == null ? null : (object)Round1(Wmi.D(cpu, "PercentProcessorTime") ?? 0),
                ["ram"] = ram,
                ["disco"] = disk,
                ["temperatura"] = Temperature(),
            };
        }

        private static double? Temperature()
        {
            var readings = Wmi.Query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\wmi")
                .Select(t => (Wmi.D(t, "CurrentTemperature") ?? 0) / 10 - 273.15)
                .Where(c => c > 0 && c < 150).ToList();
            return readings.Count == 0 ? (double?)null : Math.Round(readings.Max() * 100) / 100;
        }

        // ------------------------------------------------------------------ inventario

        private static string GigaText(double? bytes)
        {
            if (bytes == null || bytes <= 0)
                return null;
            var gb = bytes.Value / Gib;
            return (gb >= 10 ? Math.Round(gb).ToString(CultureInfo.InvariantCulture) : (Math.Round(gb * 10) / 10).ToString(CultureInfo.InvariantCulture)) + " GB";
        }

        public static Dictionary<string, object> Hardware()
        {
            var components = new List<object>();
            void Add(string tipo, string descripcion, string capacidad = null)
            {
                if (!string.IsNullOrWhiteSpace(descripcion))
                    components.Add(new Dictionary<string, object>
                    {
                        ["tipo"] = tipo,
                        ["descripcion"] = descripcion.Trim(),
                        ["capacidad"] = capacidad,
                        ["estado_salud"] = "desconocido",
                    });
            }

            foreach (var cpu in Wmi.Query("SELECT Name, NumberOfCores FROM Win32_Processor"))
                Add("cpu", Wmi.S(cpu, "Name"), Wmi.D(cpu, "NumberOfCores") is double n ? $"{n} núcleos" : null);

            var modules = Wmi.Query("SELECT Capacity, Speed, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory");
            double totalRam = modules.Sum(m => Wmi.D(m, "Capacity") ?? 0);
            if (modules.Count > 0)
            {
                var sizes = modules.Select(m => GigaText(Wmi.D(m, "Capacity")) ?? "?").ToList();
                var layout = sizes.All(s => s == sizes[0]) ? $"{sizes.Count} × {sizes[0]}" : string.Join(" + ", sizes);
                var speed = Wmi.D(modules[0], "Speed") is double mhz && mhz > 0 ? $" {mhz} MHz" : "";
                Add("ram", layout + MemoryType(modules[0]) + speed, GigaText(totalRam));
            }
            foreach (var disk in Wmi.Query("SELECT Model, Size FROM Win32_DiskDrive"))
                Add("disco", Wmi.S(disk, "Model"), GigaText(Wmi.D(disk, "Size")));
            foreach (var gpu in Wmi.Query("SELECT Name, AdapterRAM FROM Win32_VideoController"))
                Add("gpu", Wmi.S(gpu, "Name"), GigaText(Wmi.D(gpu, "AdapterRAM")));
            var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
            if (board != null)
                Add("placa_base", string.Join(" ", new[] { Wmi.S(board, "Manufacturer"), Wmi.S(board, "Product") }.Where(s => s != null)));
            var bios = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber FROM Win32_BIOS").FirstOrDefault();
            if (bios != null)
                Add("bios", string.Join(" ", new[] { Wmi.S(bios, "Manufacturer"), Wmi.S(bios, "SMBIOSBIOSVersion") }.Where(s => s != null)));
            var batteries = Wmi.Query("SELECT Name FROM Win32_Battery");
            foreach (var b in batteries)
                Add("bateria", Wmi.S(b, "Name") ?? "Batería");

            var cs = Wmi.Query("SELECT Manufacturer, Model, PCSystemType FROM Win32_ComputerSystem").FirstOrDefault();
            var os = Wmi.OperatingSystem();
            var laptop = (cs != null && Wmi.D(cs, "PCSystemType") == 2) || batteries.Count > 0;
            return new Dictionary<string, object>
            {
                ["fabricante"] = cs == null ? null : Wmi.S(cs, "Manufacturer"),
                ["modelo"] = cs == null ? null : Wmi.S(cs, "Model"),
                ["serie"] = bios == null ? null : Wmi.S(bios, "SerialNumber"),
                ["tipo"] = os.IsServer ? "servidor" : laptop ? "laptop" : "computadora",
                ["sistema_operativo"] = os.Name,
                ["ram_total_mb"] = totalRam > 0 ? (object)(long)Math.Round(totalRam / 1024 / 1024) : null,
                ["componentes"] = components,
            };
        }

        private static string MemoryType(ManagementBaseObject module)
        {
            // Windows 7 no tiene SMBIOSMemoryType: se usa MemoryType (21 DDR2, 24 DDR3).
            var code = (int)(Wmi.D(module, "SMBIOSMemoryType") ?? 0);
            if (code == 0)
                code = (int)(Wmi.D(module, "MemoryType") ?? 0);
            switch (code)
            {
                case 20: return " DDR";
                case 21: return " DDR2";
                case 24: return " DDR3";
                case 26: return " DDR4";
                case 34: return " DDR5";
                default: return "";
            }
        }

        public static Dictionary<string, object> Software()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var programs = new List<object>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null)
                        continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using (var key = uninstall.OpenSubKey(name))
                        {
                            var display = (key?.GetValue("DisplayName") as string)?.Trim();
                            if (string.IsNullOrEmpty(display) || key.GetValue("SystemComponent") is int sc && sc == 1 || key.GetValue("ParentKeyName") != null)
                                continue;
                            var version = (key.GetValue("DisplayVersion") as string)?.Trim();
                            if (!seen.Add(display + "|" + version))
                                continue;
                            programs.Add(new Dictionary<string, object>
                            {
                                ["nombre"] = display,
                                ["version"] = string.IsNullOrEmpty(version) ? null : version,
                                ["editor"] = (key.GetValue("Publisher") as string)?.Trim(),
                                ["fecha_instalacion"] = InstallDate(key.GetValue("InstallDate") as string),
                            });
                            if (programs.Count >= 5000)
                                break;
                        }
                    }
                }
            }
            return new Dictionary<string, object> { ["programas"] = programs };
        }

        private static string InstallDate(string value)
        {
            var match = Regex.Match(value?.Trim() ?? "", @"^(\d{4})(\d{2})(\d{2})$");
            if (!match.Success)
                return null;
            return DateTime.TryParseExact(match.Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 1990
                ? date.ToString("yyyy-MM-dd")
                : null;
        }

        public static Dictionary<string, object> Interfaces()
        {
            var ipByIndex = new Dictionary<uint, string>();
            foreach (var c in Wmi.Query("SELECT Index, IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled=TRUE"))
            {
                var ips = c["IPAddress"] as string[];
                var ipv4 = ips?.FirstOrDefault(ip => ip.Contains('.'));
                if (ipv4 != null && Wmi.D(c, "Index") is double index)
                    ipByIndex[(uint)index] = ipv4;
            }
            var seen = new HashSet<string>();
            var list = new List<object>();
            foreach (var a in Wmi.Query("SELECT Index, NetConnectionID, MACAddress, Name, Speed FROM Win32_NetworkAdapter WHERE PhysicalAdapter=TRUE AND MACAddress IS NOT NULL"))
            {
                var mac = Wmi.S(a, "MACAddress")?.ToUpperInvariant().Replace('-', ':');
                var name = Wmi.S(a, "NetConnectionID");
                if (mac == null || name == null || mac == "00:00:00:00:00:00" || !seen.Add(mac))
                    continue;
                var text = name + " " + Wmi.S(a, "Name");
                var speed = Wmi.D(a, "Speed");
                list.Add(new Dictionary<string, object>
                {
                    ["nombre"] = name,
                    ["mac"] = mac,
                    ["ip"] = Wmi.D(a, "Index") is double index && ipByIndex.TryGetValue((uint)index, out var ip) ? ip : null,
                    ["tipo"] = Virtual.IsMatch(text) ? "virtual" : Wifi.IsMatch(text) ? "wifi" : "cableada",
                    ["velocidad_mbps"] = speed > 0 && speed < 1e12 ? (object)(long)Math.Round(speed.Value / 1e6) : null,
                });
            }
            return new Dictionary<string, object> { ["interfaces"] = list };
        }

        // ------------------------------------------------------------------ salud

        public static Dictionary<string, object> Volumes() => new Dictionary<string, object>
        {
            ["volumenes"] = Wmi.Query("SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3")
                .Select(v => new Dictionary<string, object>
                {
                    ["unidad"] = Wmi.S(v, "DeviceID"),
                    ["etiqueta"] = Wmi.S(v, "VolumeName"),
                    ["total_gb"] = Gb(Wmi.D(v, "Size")),
                    ["libre_gb"] = Gb(Wmi.D(v, "FreeSpace")),
                })
                .Where(v => v["unidad"] is string u && Regex.IsMatch(u, "^[A-Za-z]:$") && (double)v["total_gb"] > 0)
                .Cast<object>().ToList(),
        };

        public static Dictionary<string, object> Processes()
        {
            var cores = Math.Max(1, Environment.ProcessorCount);
            var rows = Wmi.Query("SELECT Name, IDProcess, PercentProcessorTime, WorkingSetPrivate FROM Win32_PerfFormattedData_PerfProc_Process")
                .Where(p => Wmi.S(p, "Name") != "_Total" && Wmi.S(p, "Name") != "Idle")
                .Select(p => new
                {
                    Name = Regex.Replace(Wmi.S(p, "Name") ?? "?", @"#\d+$", ""),
                    Pid = (long)(Wmi.D(p, "IDProcess") ?? 0),
                    Cpu = Round1(Math.Min(100, (Wmi.D(p, "PercentProcessorTime") ?? 0) / cores)),
                    Ram = Math.Round((Wmi.D(p, "WorkingSetPrivate") ?? 0) / 1024 / 1024 * 10) / 10,
                })
                .Where(p => p.Pid > 0)
                .OrderByDescending(p => p.Cpu).ThenByDescending(p => p.Ram)
                .ToList();
            return new Dictionary<string, object>
            {
                ["total"] = rows.Count,
                ["procesos"] = rows.Take(20).Select(p => (object)new Dictionary<string, object>
                {
                    ["nombre"] = p.Name,
                    ["pid"] = p.Pid,
                    ["cpu"] = p.Cpu,
                    ["ram_mb"] = p.Ram,
                }).ToList(),
            };
        }

        public static Dictionary<string, object> Services()
        {
            var list = new List<object>();
            foreach (var s in Wmi.Query("SELECT Name, DisplayName, State, StartMode, ExitCode FROM Win32_Service WHERE StartMode='Auto'"))
            {
                var name = Wmi.S(s, "Name");
                if (name == null)
                    continue;
                var state = Wmi.S(s, "State");
                var estado = state == "Running" ? "en_ejecucion" : state == "Stopped" ? "detenido" : "otro";
                var item = new Dictionary<string, object>
                {
                    ["nombre"] = name,
                    ["nombre_visible"] = Wmi.S(s, "DisplayName"),
                    ["estado"] = estado,
                    // Windows 7 no tiene DelayedAutoStart en WMI: se lee del registro.
                    ["inicio"] = DelayedStart(name) ? "automatico_retrasado" : "automatico",
                };
                if (estado == "detenido" && Wmi.D(s, "ExitCode") is double code)
                    item["codigo_salida"] = (long)code;
                list.Add(item);
            }
            return new Dictionary<string, object> { ["servicios"] = list };
        }

        private static bool DelayedStart(string service)
        {
            using (var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}"))
                return key?.GetValue("DelayedAutostart") is int v && v == 1;
        }

        /// <summary>Errores y críticos de System y Application desde la última vez (marca por registro).</summary>
        public static Dictionary<string, object> Events()
        {
            var marks = Json.Read(File.Exists(Paths.EventsBookmarkFile) ? File.ReadAllText(Paths.EventsBookmarkFile) : "{}")
                ?? new Dictionary<string, object>();
            var since = DateTime.UtcNow.AddHours(-1);
            var events = new List<object>();
            var next = new Dictionary<string, object>();
            foreach (var log in new[] { "System", "Application" })
            {
                long last = marks.TryGetValue(log, out var m) && m != null ? Convert.ToInt64(m, CultureInfo.InvariantCulture) : 0;
                var query = new EventLogQuery(log, PathType.LogName,
                    $"*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='{since:yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
                long max = last;
                try
                {
                    using (var reader = new EventLogReader(query))
                    {
                        EventRecord record;
                        while ((record = reader.ReadEvent()) != null && events.Count < 200)
                        {
                            using (record)
                            {
                                var id = record.RecordId ?? 0;
                                if (id <= last)
                                    continue;
                                max = Math.Max(max, id);
                                string text = null;
                                try
                                {
                                    text = record.FormatDescription();
                                }
                                catch (EventLogException)
                                {
                                }
                                events.Add(new Dictionary<string, object>
                                {
                                    ["registro"] = log,
                                    ["id_registro"] = id,
                                    ["fecha"] = (record.TimeCreated ?? DateTime.Now).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                                    ["origen"] = record.ProviderName ?? "?",
                                    ["codigo"] = record.Id,
                                    ["nivel"] = record.Level == 1 ? "critico" : "error",
                                    ["mensaje"] = text == null ? null : text.Length > 1000 ? text.Substring(0, 1000) : text,
                                });
                            }
                        }
                    }
                }
                catch (EventLogException ex)
                {
                    Log.Error($"Eventos de {log}: {ex.Message}");
                }
                next[log] = max;
            }
            File.WriteAllText(Paths.EventsBookmarkFile, Json.Write(next), Encoding.UTF8);
            return new Dictionary<string, object> { ["eventos"] = events.Skip(Math.Max(0, events.Count - 100)).ToList() };
        }

        public static Dictionary<string, object> DiskHealth() => new Dictionary<string, object>
        {
            ["discos"] = Wmi.Query("SELECT Model, SerialNumber, Size, Status FROM Win32_DiskDrive")
                .Select(d => (object)new Dictionary<string, object>
                {
                    ["modelo"] = Wmi.S(d, "Model") ?? "Disco",
                    ["serie"] = Wmi.S(d, "SerialNumber"),
                    ["tamano_gb"] = Wmi.D(d, "Size") is double size ? (object)Math.Round(size / 1e9) : null,
                    // Windows informa "Pred Fail" cuando SMART anticipa una falla.
                    ["salud"] = Wmi.S(d, "Status") == "OK" ? "sano" : Wmi.S(d, "Status") == "Pred Fail" ? "critico" : "desconocido",
                }).ToList(),
        };

        public static Dictionary<string, object> Security()
        {
            var antivirus = Wmi.Query("SELECT displayName, productState FROM AntiVirusProduct", @"root\SecurityCenter2")
                .Select(a =>
                {
                    var state = (long)(Wmi.D(a, "productState") ?? 0);
                    var on = (state >> 8) & 0xFF;
                    return (object)new Dictionary<string, object>
                    {
                        ["nombre"] = Wmi.S(a, "displayName") ?? "Antivirus",
                        ["activo"] = on == 0x10 || on == 0x11,
                        ["actualizado"] = (state & 0xFF) == 0,
                    };
                }).ToList();
            bool? firewall = null;
            using (var policy = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy"))
            {
                if (policy != null)
                {
                    var profiles = new[] { "DomainProfile", "StandardProfile", "PublicProfile" }
                        .Select(p => policy.OpenSubKey(p)?.GetValue("EnableFirewall") as int?).Where(v => v.HasValue).ToList();
                    if (profiles.Count > 0)
                        firewall = profiles.All(v => v == 1);
                }
            }
            return new Dictionary<string, object> { ["antivirus"] = antivirus, ["firewall_activo"] = firewall };
        }
    }
}

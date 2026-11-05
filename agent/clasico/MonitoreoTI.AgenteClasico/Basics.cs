using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>JSON con el serializador del Framework (sin librerías externas).</summary>
    public static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 64 };

        public static string Write(object value) => Serializer.Serialize(value);

        /// <summary>Objeto JSON como diccionario; null si no es un objeto válido.</summary>
        public static Dictionary<string, object> Read(string text)
        {
            try
            {
                return Serializer.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public static string Str(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as string : null;

        public static int? Int(this Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null)
                return null;
            if (v is int i)
                return i;
            if (v is long l && l >= int.MinValue && l <= int.MaxValue)
                return (int)l;
            if (v is decimal m && m == Math.Floor(m) && m >= int.MinValue && m <= int.MaxValue)
                return (int)m;
            return null;
        }

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;
    }

    /// <summary>Un mensaje del protocolo: {v, tipo, id, ts, datos}.</summary>
    public static class Envelope
    {
        public static Dictionary<string, object> Create(string tipo, object datos) => new Dictionary<string, object>
        {
            ["v"] = 1,
            ["tipo"] = tipo,
            ["id"] = Guid.NewGuid().ToString(),
            ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datos"] = datos,
        };
    }

    /// <summary>
    /// Rutas: el programa en %ProgramFiles%\MonitoreoTI\AgenteClasico y los datos en %ProgramData%\MonitoreoTI\Agent
    /// (la misma carpeta de datos que el agente normal; nunca conviven en un equipo).
    /// </summary>
    public static class Paths
    {
        public const string ServiceName = "MonitoreoTIAgent";
        public const string ExeName = "MonitoreoTI.AgenteClasico.exe";
        public const string CaFileName = "MonitoreoTI-CA.pem";

        public static string ProgramDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MonitoreoTI", "AgenteClasico");

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MonitoreoTI", "Agent");

        public static string ConfigFile => Path.Combine(DataDirectory, "agente-clasico.json");
        public static string TrustedCaFile => Path.Combine(DataDirectory, "ca-confianza.pem");
        public static string LogFile => Path.Combine(DataDirectory, "agente-clasico.log");
        public static string SeenCommandsFile => Path.Combine(DataDirectory, "comandos-recibidos.json");
        public static string EventsBookmarkFile => Path.Combine(DataDirectory, "eventos-marca.json");
    }

    /// <summary>Registro en archivo (rota a los 2 MB). Nunca se escriben credenciales ni tokens.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static void Info(string message) => Write("INF", message);
        public static void Error(string message) => Write("ERR", message);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.DataDirectory);
                    var file = Paths.LogFile;
                    if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                    {
                        File.Copy(file, file + ".1", true);
                        File.Delete(file);
                    }
                    File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}", Encoding.UTF8);
                }
                if (Environment.UserInteractive)
                    Console.WriteLine($"{level} {message}");
            }
            catch (IOException)
            {
                // Sin disco para el registro: el agente sigue.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Configuración del agente: servidor, credencial (cifrada con DPAPI del equipo: solo este equipo la abre),
    /// identificadores y la llave pública de la empresa para los comandos firmados.
    /// </summary>
    public sealed class AgentConfig
    {
        public string Server { get; set; }
        public string Credential { get; set; }
        public int MachineId { get; set; }
        public int AgentId { get; set; }
        public string CompanyPublicKey { get; set; }

        public static AgentConfig Load()
        {
            if (!File.Exists(Paths.ConfigFile))
                return null;
            var d = Json.Read(File.ReadAllText(Paths.ConfigFile, Encoding.UTF8));
            var sealedCredential = d.Str("credencial");
            if (d == null || sealedCredential == null)
                return null;
            string credential;
            try
            {
                credential = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(sealedCredential), null, DataProtectionScope.LocalMachine));
            }
            catch (CryptographicException)
            {
                return null;
            }
            return new AgentConfig
            {
                Server = d.Str("servidor"),
                Credential = credential,
                MachineId = d.Int("maquinaId") ?? 0,
                AgentId = d.Int("agenteId") ?? 0,
                CompanyPublicKey = d.Str("llaveEmpresa"),
            };
        }

        public void Save()
        {
            Directory.CreateDirectory(Paths.DataDirectory);
            var sealedCredential = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Credential), null, DataProtectionScope.LocalMachine));
            var text = Json.Write(new Dictionary<string, object>
            {
                ["servidor"] = Server,
                ["credencial"] = sealedCredential,
                ["maquinaId"] = MachineId,
                ["agenteId"] = AgentId,
                ["llaveEmpresa"] = CompanyPublicKey,
            });
            var temp = Paths.ConfigFile + ".tmp";
            File.WriteAllText(temp, text, Encoding.UTF8);
            if (File.Exists(Paths.ConfigFile))
                File.Delete(Paths.ConfigFile);
            File.Move(temp, Paths.ConfigFile);
        }
    }

    public static class AgentInfo
    {
        /// <summary>
        /// Versión que ve el servidor. "-clasico": nunca se le ofrece el instalador del agente normal, y al ser 0.1 no
        /// recibe funciones de recolector (SNMP, barridos, API, instalación remota).
        /// </summary>
        public const string Version = "0.1.0-clasico";
    }
}

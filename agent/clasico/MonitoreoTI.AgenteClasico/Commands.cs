using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Comandos firmados (sección 8.1), igual que el agente normal: solo se ejecuta lo firmado con la llave de la empresa
    /// (ECDSA P-256), para esta máquina, sin vencer y una sola vez. PowerShell en Windows 7 es la versión 2.0.
    /// </summary>
    public sealed class Commands
    {
        private const int MaxOutput = 1024 * 1024;
        private readonly AgentConfig _config;
        private readonly Action<string, object> _submit;
        private readonly List<long> _seen;

        public Commands(AgentConfig config, Action<string, object> submit)
        {
            _config = config;
            _submit = submit;
            try
            {
                _seen = File.Exists(Paths.SeenCommandsFile)
                    ? (new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<List<long>>(File.ReadAllText(Paths.SeenCommandsFile)) ?? new List<long>())
                    : new List<long>();
            }
            catch (Exception)
            {
                _seen = new List<long>();
            }
        }

        public static string SignedPayload(int id, int machine, string tipo, string language, int timeout, string expires, string content)
        {
            string hash;
            using (var sha = SHA256.Create())
                hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(content)).Select(b => b.ToString("x2")));
            return string.Join("\n", "monitoreoti/comando/v1", id.ToString(CultureInfo.InvariantCulture), machine.ToString(CultureInfo.InvariantCulture),
                tipo, language, timeout.ToString(CultureInfo.InvariantCulture), expires, hash);
        }

        /// <summary>Llave pública SPKI (DER, base64) P-256 y firma IEEE P1363 (r‖s), como el servidor.</summary>
        public static bool Verify(string publicKeyBase64, string payload, string signatureBase64)
        {
            try
            {
                var spki = Convert.FromBase64String(publicKeyBase64);
                var signature = Convert.FromBase64String(signatureBase64);
                if (spki.Length < 65 || signature.Length != 64)
                    return false;
                var point = spki.Skip(spki.Length - 65).ToArray();
                if (point[0] != 0x04)
                    return false;
                // BCRYPT_ECCPUBLIC_BLOB: "ECS1", largo de cada coordenada, X, Y.
                var blob = new byte[8 + 64];
                BitConverter.GetBytes(0x31534345).CopyTo(blob, 0);
                BitConverter.GetBytes(32).CopyTo(blob, 4);
                Buffer.BlockCopy(point, 1, blob, 8, 64);
                using (var key = CngKey.Import(blob, CngKeyBlobFormat.EccPublicBlob))
                using (var ecdsa = new ECDsaCng(key) { HashAlgorithm = CngAlgorithm.Sha256 })
                    return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), signature);
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException)
            {
                return false;
            }
        }

        public async Task HandleAsync(Dictionary<string, object> datos)
        {
            var id = datos.Int("comando_id");
            var machine = datos.Int("maquina_id");
            var tipo = datos.Str("tipo");
            var language = datos.Str("lenguaje");
            var content = datos.Str("contenido");
            var timeout = datos.Int("timeout_s");
            var expires = datos.Str("vence");
            var signature = datos.Str("firma");
            if (id == null || machine == null || tipo == null || language == null || content == null || timeout == null || expires == null || signature == null)
            {
                Log.Error("Comando con formato inválido; se descarta.");
                return;
            }
            if (_seen.Contains(id.Value))
                return;

            string reason = null;
            if (machine != _config.MachineId)
                reason = "el comando es para otro equipo";
            else if (string.IsNullOrEmpty(_config.CompanyPublicKey))
                reason = "el agente no tiene la llave de la empresa";
            else if (!Verify(_config.CompanyPublicKey, SignedPayload(id.Value, machine.Value, tipo, language, timeout.Value, expires, content), signature))
                reason = "firma no válida";
            else if (!DateTime.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until) || until <= DateTime.UtcNow)
                reason = "vencido";
            if (reason != null)
            {
                Log.Error($"Comando {id} rechazado: {reason}.");
                Report(id.Value, new Dictionary<string, object> { ["estado"] = "rechazado", ["error"] = reason });
                return;
            }

            Remember(id.Value);
            Report(id.Value, new Dictionary<string, object> { ["estado"] = "ejecutando" });
            Log.Info($"Ejecutando comando {id} ({language}).");
            try
            {
                var outcome = await Task.Run(() => Run(language, content, timeout.Value)).ConfigureAwait(false);
                Report(id.Value, new Dictionary<string, object>
                {
                    ["estado"] = outcome.TimedOut || outcome.ExitCode != 0 ? "error" : "completado",
                    ["codigoSalida"] = outcome.ExitCode,
                    ["salida"] = outcome.Output,
                    ["error"] = outcome.TimedOut ? $"Se detuvo al pasar el tiempo límite ({timeout} s)." : null,
                });
            }
            catch (Exception ex)
            {
                Report(id.Value, new Dictionary<string, object> { ["estado"] = "error", ["error"] = ex.Message });
            }
        }

        private void Report(int id, Dictionary<string, object> data)
        {
            data["comandoId"] = id;
            _submit("resultado_comando", data);
        }

        private void Remember(long id)
        {
            _seen.Add(id);
            while (_seen.Count > 500)
                _seen.RemoveAt(0);
            File.WriteAllText(Paths.SeenCommandsFile, Json.Write(_seen), Encoding.UTF8);
        }

        private sealed class Outcome
        {
            public int? ExitCode;
            public string Output;
            public bool TimedOut;
        }

        private static Outcome Run(string language, string content, int timeoutSeconds)
        {
            var temp = Path.Combine(Paths.DataDirectory, "tmp");
            Directory.CreateDirectory(temp);
            var basePath = Path.Combine(temp, $"cmd-{DateTime.UtcNow.Ticks}");
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string file, exe, args;
            switch (language)
            {
                case "powershell":
                    file = basePath + ".ps1";
                    File.WriteAllText(file, content, new UTF8Encoding(true));
                    exe = Path.Combine(system32, @"WindowsPowerShell\v1.0\powershell.exe");
                    args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"";
                    break;
                case "cmd":
                    file = basePath + ".cmd";
                    File.WriteAllText(file, "@echo off\r\n" + content.Replace("\r\n", "\n").Replace("\n", "\r\n"), Encoding.Default);
                    exe = Path.Combine(system32, "cmd.exe");
                    args = $"/d /c \"{file}\"";
                    break;
                default:
                    throw new InvalidOperationException($"Lenguaje no disponible en el agente clásico: {language}.");
            }
            try
            {
                var output = new StringBuilder();
                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                })
                {
                    DataReceivedEventHandler append = (s, e) =>
                    {
                        if (e.Data == null)
                            return;
                        lock (output)
                            if (output.Length < MaxOutput)
                                output.AppendLine(e.Data);
                    };
                    process.OutputDataReceived += append;
                    process.ErrorDataReceived += append;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(Math.Max(1, timeoutSeconds) * 1000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        return new Outcome { ExitCode = null, Output = output.ToString(), TimedOut = true };
                    }
                    process.WaitForExit();
                    return new Outcome { ExitCode = process.ExitCode, Output = output.ToString() };
                }
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

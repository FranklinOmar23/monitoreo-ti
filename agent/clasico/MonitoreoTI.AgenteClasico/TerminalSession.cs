using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Terminal remota (decisión 84), el mismo protocolo que el agente normal:
    /// del panel {"tipo":"iniciar","shell"} y {"tipo":"tamano"}; en binario, lo que escribe el técnico. Al panel
    /// {"tipo":"listo"}, {"tipo":"fin","codigo"} o {"tipo":"error"}; en binario, la salida.
    /// <para>
    /// Windows 7 no tiene la consola moderna (ConPTY): el shell corre con tuberías y el agente hace de editor de línea
    /// (eco, borrar, historial con las flechas, Ctrl+C corta lo que está corriendo) y escribe el indicador después de
    /// cada comando. Los programas de pantalla completa (edit, more interactivo) no se ven bien; los comandos normales sí.
    /// </para>
    /// </summary>
    public sealed class TerminalSession : ISessionHandler
    {
        private readonly SessionChannel _channel;
        private readonly StringBuilder _line = new StringBuilder();
        private readonly List<string> _history = new List<string>();
        private readonly object _gate = new object();
        private Process _shell;
        private Encoding _encoding;
        private bool _powerShell;
        private int _historyIndex;
        private bool _disposed;
        private string _escape = "";

        public TerminalSession(SessionChannel channel) => _channel = channel;

        public void OnText(string text)
        {
            var message = Json.Read(text);
            if (message.Str("tipo") == "iniciar" && _shell == null)
                Start(message.Str("shell") == "cmd" ? "cmd" : "powershell");
            // "tamano": con tuberías no hay ventana que cambiar.
        }

        private void Start(string shell)
        {
            _powerShell = shell == "powershell";
            _encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var system = Environment.SystemDirectory;
            var info = _powerShell
                ? new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"), "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -")
                : new ProcessStartInfo(Path.Combine(system, "cmd.exe"), "/Q");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = _encoding;
            info.StandardErrorEncoding = _encoding;
            info.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows).Substring(0, 3);
            try
            {
                _shell = Process.Start(info);
            }
            catch (Exception ex)
            {
                Fail($"No se pudo abrir la terminal: {ex.Message}");
                return;
            }
            Pump(_shell.StandardOutput);
            Pump(_shell.StandardError);
            _shell.EnableRaisingEvents = true;
            _shell.Exited += (_, __) =>
            {
                if (_disposed)
                    return;
                _channel.SendText(Json.Write(new Dictionary<string, object> { ["tipo"] = "fin", ["codigo"] = SafeExitCode() }));
                _channel.Close();
            };
            _channel.SendText(Json.Write(new Dictionary<string, object> { ["tipo"] = "listo", ["shell"] = shell }));
            Output($"Terminal de {Environment.MachineName} ({(_powerShell ? "PowerShell" : "cmd")}, Windows 7/8: sin colores ni programas de pantalla completa).\r\n");
            SendPrompt();
            Log.Info($"Terminal abierta ({shell}).");
        }

        private int SafeExitCode()
        {
            try
            {
                return _shell.ExitCode;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private void Pump(StreamReader reader)
        {
            Task.Run(async () =>
            {
                var buffer = new char[4096];
                int read;
                try
                {
                    while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        Output(new string(buffer, 0, read));
                }
                catch (Exception)
                {
                    // El shell terminó.
                }
            });
        }

        /// <summary>Al visor en UTF-8, con los saltos de línea como los espera la terminal (CR LF).</summary>
        private void Output(string text)
        {
            var fixedLines = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _channel.SendBinary(Encoding.UTF8.GetBytes(fixedLines));
        }

        /// <summary>El indicador (carpeta actual): lo escribe el propio shell después de cada comando.</summary>
        private void SendPrompt() => Write(_powerShell
            ? "Write-Host -NoNewline ('PS ' + (Get-Location).ProviderPath + '> ')"
            : "<nul set /p \"=%CD%>\"");

        private void Write(string line)
        {
            try
            {
                lock (_gate)
                {
                    var bytes = _encoding.GetBytes(line + "\r\n");
                    _shell.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    _shell.StandardInput.BaseStream.Flush();
                }
            }
            catch (Exception)
            {
                // El shell terminó: el evento Exited cierra la sesión.
            }
        }

        public void OnBinary(byte[] data)
        {
            if (_shell == null)
                return;
            foreach (var ch in Encoding.UTF8.GetString(data))
                Key(ch);
        }

        /// <summary>Editor de línea: lo que escribe el técnico se ve al instante y va al shell con Enter.</summary>
        private void Key(char ch)
        {
            // Secuencias de escape de las flechas: ESC [ A/B/C/D.
            if (_escape.Length > 0 || ch == '\x1b')
            {
                _escape += ch;
                if (_escape.Length < 3)
                    return;
                var code = _escape[2];
                _escape = "";
                if (code == 'A' || code == 'B')
                    Recall(code == 'A' ? -1 : 1);
                return;
            }
            switch (ch)
            {
                case '\r':
                {
                    var command = _line.ToString();
                    _line.Clear();
                    Output("\r\n");
                    if (command.Trim().Length > 0 && (_history.Count == 0 || _history[_history.Count - 1] != command))
                        _history.Add(command);
                    _historyIndex = _history.Count;
                    if (command.Trim().Length > 0)
                        Write(command);
                    SendPrompt();
                    return;
                }
                case '\n':
                    return;
                case '\x7f':
                case '\b':
                    if (_line.Length > 0)
                    {
                        _line.Length--;
                        Output("\b \b");
                    }
                    return;
                case '\x03': // Ctrl+C: corta lo que esté corriendo (no el shell).
                    _line.Clear();
                    Output("^C\r\n");
                    StopChildren(_shell.Id);
                    SendPrompt();
                    return;
                case '\t':
                    return;
                default:
                    if (ch < ' ')
                        return;
                    _line.Append(ch);
                    Output(ch.ToString());
                    return;
            }
        }

        private void Recall(int step)
        {
            if (_history.Count == 0)
                return;
            _historyIndex = Math.Max(0, Math.Min(_history.Count, _historyIndex + step));
            var text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
            // Borra lo escrito y pone la línea del historial.
            Output(new string('\b', _line.Length) + new string(' ', _line.Length) + new string('\b', _line.Length) + text);
            _line.Clear().Append(text);
        }

        /// <summary>Termina los procesos que lanzó el shell (lo que corre en primer plano).</summary>
        private static void StopChildren(int parentId)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentId}"))
                {
                    foreach (ManagementObject child in searcher.Get())
                    {
                        using (child)
                        {
                            var id = Convert.ToInt32(child["ProcessId"]);
                            StopChildren(id);
                            try
                            {
                                Process.GetProcessById(id).Kill();
                            }
                            catch (Exception)
                            {
                            }
                        }
                    }
                }
            }
            catch (ManagementException)
            {
            }
        }

        private void Fail(string text)
        {
            _channel.SendText(Json.Write(new Dictionary<string, object> { ["tipo"] = "error", ["mensaje"] = text }));
            _channel.Close();
        }

        public void Dispose()
        {
            _disposed = true;
            var shell = _shell;
            _shell = null;
            if (shell == null)
                return;
            try
            {
                StopChildren(shell.Id);
                if (!shell.HasExited)
                    shell.Kill();
            }
            catch (Exception)
            {
            }
            shell.Dispose();
        }
    }
}

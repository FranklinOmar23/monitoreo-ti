using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MonitoreoTI.AgenteClasico
{
    /// <summary>
    /// Explorador de archivos remoto (decisión 84), el mismo protocolo que el agente normal. Pedidos en texto con un id:
    /// listar (ruta "" = unidades), descargar, ack, subir, subir_fin, cancelar, crear_carpeta, eliminar y renombrar.
    /// Partes binarias: [id:4, big endian][hasta 256 KB]. Corre como SYSTEM; el contenido nunca se registra y cada
    /// operación se informa para la auditoría.
    /// </summary>
    public sealed class FileSession : ISessionHandler
    {
        public const int ChunkBytes = 256 * 1024;
        public const int Window = 4;
        private const int MaxEntries = 5000;
        private const string PartSuffix = ".monitoreoti-parte";

        private readonly SessionChannel _channel;
        private readonly Action<Dictionary<string, object>> _report;
        private readonly Func<byte[], byte[]> _wrapBinary;
        private readonly ConcurrentDictionary<int, Download> _downloads = new ConcurrentDictionary<int, Download>();
        private readonly ConcurrentDictionary<int, Upload> _uploads = new ConcurrentDictionary<int, Upload>();
        private readonly object _gate = new object();

        /// <param name="wrapBinary">Dentro del escritorio, las partes van con su marcador delante.</param>
        public FileSession(SessionChannel channel, Action<Dictionary<string, object>> report, Func<byte[], byte[]> wrapBinary = null)
        {
            _channel = channel;
            _report = report;
            _wrapBinary = wrapBinary ?? (data => data);
        }

        private sealed class Download
        {
            public SemaphoreSlim Credits = new SemaphoreSlim(Window, Window);
            public CancellationTokenSource Cancel = new CancellationTokenSource();
        }

        private sealed class Upload
        {
            public FileStream Stream;
            public string Target;
            public string Part;
            public long Size;
            public long Received;
        }

        public void OnText(string text)
        {
            var request = Json.Read(text);
            var op = request.Str("op");
            var id = request.Int("id");
            if (op == null || id == null)
                return;
            try
            {
                lock (_gate)
                    Handle(op, id.Value, request);
            }
            catch (Exception ex)
            {
                Reply(id.Value, new Dictionary<string, object> { ["ok"] = false, ["error"] = Describe(ex) });
            }
        }

        public void OnBinary(byte[] data)
        {
            if (data.Length < 4)
                return;
            var id = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
            if (!_uploads.TryGetValue(id, out var upload))
                return;
            var length = data.Length - 4;
            if (upload.Received + length > upload.Size)
            {
                Abort(id, "El archivo recibido es más grande de lo anunciado.");
                return;
            }
            try
            {
                lock (_gate)
                {
                    upload.Stream.Write(data, 4, length);
                    upload.Received += length;
                }
                Reply(id, new Dictionary<string, object> { ["recibido"] = upload.Received });
            }
            catch (Exception ex)
            {
                Abort(id, Describe(ex));
            }
        }

        private void Handle(string op, int id, Dictionary<string, object> r)
        {
            switch (op)
            {
                case "listar":
                    List(id, r.Str("ruta") ?? "");
                    return;
                case "descargar":
                    StartDownload(id, Checked(r.Str("ruta")));
                    return;
                case "ack":
                    if (_downloads.TryGetValue(id, out var download))
                    {
                        try
                        {
                            download.Credits.Release();
                        }
                        catch (SemaphoreFullException)
                        {
                        }
                    }
                    return;
                case "subir":
                {
                    var target = Checked(r.Str("ruta"));
                    var size = r.Long("tamano") ?? -1;
                    if (size < 0)
                        throw new InvalidOperationException("Tamaño inválido.");
                    if (!r.Bool("reemplazar") && (File.Exists(target) || Directory.Exists(target)))
                        throw new InvalidOperationException("Ya existe un archivo con ese nombre.");
                    var part = target + PartSuffix;
                    _uploads[id] = new Upload { Stream = new FileStream(part, FileMode.Create, FileAccess.Write), Target = target, Part = part, Size = size };
                    Reply(id, Ok());
                    return;
                }
                case "subir_fin":
                {
                    if (!_uploads.TryRemove(id, out var upload))
                        throw new InvalidOperationException("No hay una subida en curso con ese id.");
                    upload.Stream.Dispose();
                    if (upload.Received != upload.Size)
                    {
                        TryDelete(upload.Part);
                        throw new InvalidOperationException($"Llegaron {upload.Received} de {upload.Size} bytes; no se guardó.");
                    }
                    if (File.Exists(upload.Target))
                        File.Delete(upload.Target);
                    File.Move(upload.Part, upload.Target);
                    _report(Activity("subir", upload.Target, null, upload.Size));
                    var done = Ok();
                    done["fin"] = true;
                    Reply(id, done);
                    return;
                }
                case "cancelar":
                    if (_downloads.TryRemove(id, out var running))
                        running.Cancel.Cancel();
                    if (_uploads.ContainsKey(id))
                        Abort(id, null);
                    return;
                case "crear_carpeta":
                {
                    var path = Checked(r.Str("ruta"));
                    if (Directory.Exists(path) || File.Exists(path))
                        throw new InvalidOperationException("Ya existe.");
                    Directory.CreateDirectory(path);
                    _report(Activity("crear_carpeta", path, null, null));
                    Reply(id, Ok());
                    return;
                }
                case "renombrar":
                {
                    var path = Checked(r.Str("ruta"));
                    var name = r.Str("nombre") ?? "";
                    if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0)
                        throw new InvalidOperationException("El nombre nuevo no es válido.");
                    var target = Path.Combine(Path.GetDirectoryName(path) ?? path, name);
                    if (File.Exists(target) || Directory.Exists(target))
                        throw new InvalidOperationException("Ya existe algo con ese nombre.");
                    if (Directory.Exists(path))
                        Directory.Move(path, target);
                    else
                        File.Move(path, target);
                    _report(Activity("renombrar", path, target, null));
                    Reply(id, Ok());
                    return;
                }
                case "eliminar":
                {
                    var path = Checked(r.Str("ruta"));
                    if (Path.GetPathRoot(path)?.TrimEnd('\\') == path.TrimEnd('\\'))
                        throw new InvalidOperationException("No se puede eliminar una unidad completa.");
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                    else if (File.Exists(path))
                        File.Delete(path);
                    else
                        throw new FileNotFoundException("No existe.");
                    _report(Activity("eliminar", path, null, null));
                    Reply(id, Ok());
                    return;
                }
                default:
                    Reply(id, new Dictionary<string, object> { ["ok"] = false, ["error"] = $"Operación desconocida: {op}" });
                    return;
            }
        }

        private void List(int id, string path)
        {
            var entries = new List<object>();
            if (path == "")
            {
                foreach (var drive in DriveInfo.GetDrives())
                    entries.Add(Entry(drive.Name, "unidad", null, null));
                var drives = Ok();
                drives["ruta"] = "";
                drives["entradas"] = entries;
                Reply(id, drives);
                return;
            }
            var folder = Checked(path);
            var items = new DirectoryInfo(folder).EnumerateFileSystemInfos().Take(MaxEntries + 1).ToList();
            foreach (var item in items.Take(MaxEntries))
            {
                // Lo que no se puede leer (en uso, sin permiso) se muestra sin datos.
                long? size = null;
                string modified = null;
                try
                {
                    if (item is FileInfo file)
                        size = file.Length;
                    modified = item.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                }
                catch (Exception)
                {
                }
                entries.Add(Entry(item.Name, item is DirectoryInfo ? "carpeta" : "archivo", size, modified));
            }
            var reply = Ok();
            reply["ruta"] = folder;
            reply["entradas"] = entries;
            reply["recortado"] = items.Count > MaxEntries;
            Reply(id, reply);
        }

        private static Dictionary<string, object> Entry(string name, string type, long? size, string modified) => new Dictionary<string, object>
        {
            ["nombre"] = name,
            ["tipo"] = type,
            ["tamano"] = size,
            ["modificado"] = modified,
        };

        private void StartDownload(int id, string path)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException(Directory.Exists(path) ? "Solo se pueden descargar archivos." : "No existe.");
            var size = new FileInfo(path).Length;
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var download = new Download();
            _downloads[id] = download;
            var reply = Ok();
            reply["tamano"] = size;
            Reply(id, reply);
            _report(Activity("descargar", path, null, size));
            Task.Run(async () =>
            {
                try
                {
                    using (stream)
                    {
                        var buffer = new byte[ChunkBytes];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // Control de flujo: como mucho Window partes sin confirmar en camino.
                            await download.Credits.WaitAsync(download.Cancel.Token).ConfigureAwait(false);
                            var part = new byte[4 + read];
                            part[0] = (byte)(id >> 24);
                            part[1] = (byte)(id >> 16);
                            part[2] = (byte)(id >> 8);
                            part[3] = (byte)id;
                            Buffer.BlockCopy(buffer, 0, part, 4, read);
                            _channel.SendBinary(_wrapBinary(part));
                        }
                    }
                    Reply(id, new Dictionary<string, object> { ["fin"] = true });
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Reply(id, new Dictionary<string, object> { ["ok"] = false, ["error"] = Describe(ex) });
                }
                finally
                {
                    _downloads.TryRemove(id, out _);
                }
            });
        }

        private void Abort(int id, string error)
        {
            if (!_uploads.TryRemove(id, out var upload))
                return;
            try
            {
                upload.Stream.Dispose();
            }
            catch (Exception)
            {
            }
            TryDelete(upload.Part);
            if (error != null)
                Reply(id, new Dictionary<string, object> { ["ok"] = false, ["error"] = error });
        }

        private void Reply(int id, Dictionary<string, object> data)
        {
            data["id"] = id;
            _channel.SendText(Json.Write(data));
        }

        private static Dictionary<string, object> Ok() => new Dictionary<string, object> { ["ok"] = true };

        private static Dictionary<string, object> Activity(string action, string path, string target, long? size)
        {
            var a = new Dictionary<string, object> { ["accion"] = action, ["ruta"] = path };
            if (target != null)
                a["destino"] = target;
            if (size != null)
                a["tamano"] = size.Value;
            return a;
        }

        private static string Checked(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path))
                throw new InvalidOperationException("La ruta debe ser completa (por ejemplo C:\\Users).");
            return Path.GetFullPath(path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }

        private static string Describe(Exception ex)
        {
            switch (ex)
            {
                case FileNotFoundException _:
                case DirectoryNotFoundException _:
                    return "No existe.";
                case UnauthorizedAccessException _:
                    return "Sin permiso (o el archivo está en uso).";
                case IOException io when (io.HResult & 0xFFFF) == 32:
                    return "El archivo está en uso.";
                case IOException io when (io.HResult & 0xFFFF) == 112:
                    return "No hay espacio en el disco.";
                default:
                    return ex.Message;
            }
        }

        public void Dispose()
        {
            foreach (var d in _downloads.Values)
                d.Cancel.Cancel();
            _downloads.Clear();
            foreach (var id in _uploads.Keys.ToList())
                Abort(id, null);
        }
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace MonitoreoTI.AgentHost.Backup;

/// <summary>
/// "MonitoreoTI.AgentHost.exe respaldo" (decisión 82): el agente manda el trabajo por stdin (con la cuenta de la carpeta
/// de red ya abierta, solo en memoria) y aquí se hace el respaldo, informando el avance en una línea JSON por paso y al
/// final {"fin":true,"estado":"correcto"|"advertencia"|"error",…}.
///
/// Pasos: abrir el destino → (en frío) detener servicios → instantánea o BACKUP DATABASE → (en frío) arrancarlos apenas
/// los datos quedan fijos → copiar al destino → verificar → manifiesto → retención → expulsar el disco.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BackupRunner
{
    private readonly TextWriter _output;
    private readonly BackupRequest _request;
    private readonly Stopwatch _sinceProgress = Stopwatch.StartNew();
    private readonly List<string> _warnings = [];
    private long _copied;
    private long _total;
    private int _files;
    private string? _kind;
    private string? _destination;
    private string? _setName;

    private BackupRunner(BackupRequest request, TextWriter output)
    {
        _request = request;
        _output = output;
    }

    private BackupJob Job => _request.Trabajo;

    public static async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        BackupRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<BackupRequest>(await input.ReadToEndAsync(), BackupJson.Options);
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request is null || request.Trabajo.Id <= 0 || request.Trabajo.Elementos.Count == 0)
        {
            Write(output, new BackupStep { Estado = "error", Detalle = "Pedido de respaldo inválido.", Fin = true });
            return 2;
        }
        return await new BackupRunner(request, output).RunAsync();
    }

    private static void Write(TextWriter output, BackupStep step)
    {
        output.WriteLine(JsonSerializer.Serialize(step, BackupJson.Options));
        output.Flush();
    }

    private void Progress(string detail, bool force = false)
    {
        if (!force && _sinceProgress.Elapsed < TimeSpan.FromSeconds(3))
            return;
        _sinceProgress.Restart();
        Write(_output, new BackupStep
        {
            Estado = "en_curso", Tipo = _kind, Detalle = detail, BytesCopiados = _copied, BytesTotales = _total, Archivos = _files,
            Destino = _destination, Conjunto = _setName,
        });
    }

    private async Task<int> RunAsync()
    {
        var cold = Job.Modo == "frio" ? new ColdServices() : null;
        var notRestarted = new List<string>();
        string? error = null;
        OpenDestination? destination = null;
        string? setFolder = null;
        var completed = false;
        try
        {
            Progress("Abriendo el destino…", force: true);
            destination = BackupDestinations.Open(Job.Destino);
            _destination = destination.Description;
            var jobFolder = BackupCatalog.JobFolder(destination.Root, _request.Equipo, Job.Id, Job.Nombre);
            Directory.CreateDirectory(jobFolder);
            var sets = BackupCatalog.Read(jobFolder);

            // En frío, el tiempo máximo corre desde que se detienen los servicios hasta que vuelven a arrancar.
            using var coldLimit = new CancellationTokenSource();
            void ReleaseServices()
            {
                if (cold is null)
                    return;
                notRestarted.AddRange(cold.Restart(d => Progress(d, force: true)));
                coldLimit.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            if (cold is not null)
            {
                cold.Stop(Job.ServiciosDetener, d => Progress(d, force: true));
                coldLimit.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, Job.FrioMaxMinutos)));
            }

            BackupManifest manifest;
            try
            {
                manifest = Job.Origen == "sql"
                    ? await SqlAsync(jobFolder, sets, ReleaseServices, coldLimit.Token, f => setFolder = f)
                    : await FoldersAsync(jobFolder, sets, ReleaseServices, coldLimit.Token, f => setFolder = f);
            }
            catch (OperationCanceledException) when (coldLimit.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"El respaldo pasó del tiempo máximo en frío ({Job.FrioMaxMinutos} min): se canceló y se volvieron a arrancar los servicios.");
            }
            finally
            {
                ReleaseServices();
            }

            BackupCatalog.WriteManifest(setFolder!, manifest);
            completed = true;
            Retention(jobFolder, setFolder!);
            if (destination.Disk is { } disk && Job.Destino.Expulsar)
            {
                destination.Dispose();
                if (BackupDestinations.Eject(disk) is { } ejectProblem)
                    _warnings.Add(ejectProblem);
            }
        }
        catch (Exception ex) when (ex is DestinationException or InvalidOperationException or IOException or UnauthorizedAccessException
            or SqlException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException or System.Management.ManagementException)
        {
            error = ex switch
            {
                SqlException sql => SqlBackup.Explain(sql),
                IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) =>
                    $"El destino se quedó sin espacio ({_destination}).",
                System.ServiceProcess.TimeoutException => "Un servicio no se detuvo a tiempo; el respaldo no se hizo.",
                _ => ex.Message,
            };
        }
        finally
        {
            if (cold is not null)
                notRestarted.AddRange(cold.Restart(d => Progress(d, force: true)));
            destination?.Dispose();
            // Un respaldo que no terminó no deja basura: sin manifiesto no cuenta, y se borra.
            if (!completed && setFolder is not null)
                TryDeleteDirectory(setFolder);
        }

        if (notRestarted.Count > 0)
            _warnings.Insert(0, $"No volvieron a arrancar: {string.Join(", ", notRestarted)}.");
        var status = error is not null ? "error" : _warnings.Count > 0 ? "advertencia" : "correcto";
        Write(_output, new BackupStep
        {
            Estado = status, Tipo = _kind, BytesCopiados = _copied, BytesTotales = _total, Archivos = _files, Destino = _destination,
            Conjunto = completed ? _setName : null, ServiciosSinArrancar = notRestarted.Count > 0 ? notRestarted.Distinct().ToList() : null,
            Detalle = Cut(string.Join(" ", new[] { error }.Concat(_warnings).Where(t => !string.IsNullOrEmpty(t))), 1000) is { Length: > 0 } text
                ? text : Summary(),
            Fin = true,
        });
        return error is null ? 0 : 1;
    }

    private string Summary() => _kind switch
    {
        "completo" => $"Copia completa: {Size(_copied)}.",
        "diferencial" => $"Diferencial: {Size(_copied)} de {Size(_total)}.",
        _ => $"Solo lo nuevo: {_files} archivos, {Size(_copied)} de {Size(_total)}.",
    };

    // ------------------------------------------------------------------ SQL Server

    private async Task<BackupManifest> SqlAsync(
        string jobFolder, List<(BackupSet Set, BackupManifest Manifest)> sets, Action release, CancellationToken ct, Action<string> setFolderCreated)
    {
        var instance = Job.InstanciaSql ?? "MSSQLSERVER";
        var described = await SqlBackup.DescribeAsync(instance, ct);
        if (described.Error is not null)
            throw new InvalidOperationException(described.Error);
        var missing = Job.Elementos.Where(e => described.Databases.All(d => !d.Name.Equals(e, StringComparison.OrdinalIgnoreCase))).ToList();
        if (missing.Count == Job.Elementos.Count)
            throw new InvalidOperationException($"No existe ninguna de las bases ({string.Join(", ", missing)}) en {instance}.");
        if (missing.Count > 0)
            _warnings.Add($"No se encontraron estas bases: {string.Join(", ", missing)}.");
        var databases = described.Databases.Where(d => Job.Elementos.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        _total = databases.Sum(d => d.SizeMb) * 1024 * 1024;

        // ¿Diferencial? Solo si la última completa de este destino sigue siendo la base de cada base de datos.
        var nowUtc = DateTime.UtcNow;
        var full = BackupCatalog.NeedsFull(sets.Select(s => s.Set).ToList(), Job.Estrategia, Job.CompletoCadaDias, _request.ForzarCompleto, nowUtc);
        var baseSet = sets.LastOrDefault(s => s.Set.Kind == "completo");
        if (!full && baseSet.Manifest is { } baseManifest)
        {
            foreach (var db in databases)
            {
                var recorded = baseManifest.Bases?.FirstOrDefault(b => b.Nombre.Equals(db.Name, StringComparison.OrdinalIgnoreCase))?.BaseGuid;
                var current = await SqlBackup.DifferentialBaseAsync(instance, db.Name, ct);
                if (recorded is null)
                {
                    full = true;
                    break;
                }
                if (!string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase))
                {
                    full = true;
                    _warnings.Add($"Otro programa hizo una copia completa de {db.Name} después de la última de Centinela, así que esta fue completa. " +
                        "Si ese otro respaldo sigue, configúrelo con COPY_ONLY o deje que Centinela lo reemplace.");
                    break;
                }
            }
        }
        _kind = full ? "completo" : "diferencial";
        _setName = BackupCatalog.SetName(DateTime.Now, _kind);
        var setFolder = Path.Combine(jobFolder, _setName);
        Directory.CreateDirectory(setFolder);
        setFolderCreated(setFolder);

        var prepared = await SqlBackup.PrepareAsync(instance, ct);
        var staged = new List<(SqlDatabase Db, string File, string? Guid)>();
        try
        {
            foreach (var db in databases)
            {
                Progress($"Respaldando la base {db.Name} ({_kind})…", force: true);
                var file = Path.Combine(prepared.StagingFolder, $"Centinela_{Job.Id}_{BackupCatalog.Safe(db.Name)}_{(_request.Ejecucion + "00000000")[..8]}.bak");
                // "Completo cada vez" no corta la cadena de otros programas (COPY_ONLY); el completo de "solo lo nuevo" es la base.
                await SqlBackup.BackupAsync(prepared, db.Name, file, _kind, copyOnly: Job.Estrategia != "incremental", Job.Verificar, ct);
                var guid = _kind == "completo" && Job.Estrategia == "incremental" ? await SqlBackup.DifferentialBaseAsync(instance, db.Name, ct) : null;
                staged.Add((db, file, guid));
            }
            // Los datos ya quedaron fijos en los .bak: en frío, los servicios vuelven ahora (la copia al destino no los necesita).
            release();

            var list = new List<ManifestDatabase>();
            foreach (var (db, file, guid) in staged)
            {
                var name = BackupCatalog.Safe(db.Name) + ".bak";
                Progress($"Copiando {db.Name} al destino…", force: true);
                var bytes = await CopyAsync(file, Path.Combine(setFolder, name), CancellationToken.None);
                _files++;
                list.Add(new ManifestDatabase { Nombre = db.Name, Archivo = name, Tipo = _kind, BaseGuid = guid, Bytes = bytes });
            }
            return new BackupManifest
            {
                TrabajoId = Job.Id, Conjunto = _setName, Fecha = DateTime.UtcNow, Tipo = _kind, Origen = "sql",
                Base = full ? null : baseSet.Set.Name,
                Depende = full ? [_setName] : [baseSet.Set.Name, _setName],
                Elementos = databases.Select(d => d.Name).ToList(), BytesCopiados = _copied, BytesTotales = _total, Bases = list,
            };
        }
        finally
        {
            foreach (var (_, file, _) in staged)
                TryDelete(file);
        }
    }

    // ------------------------------------------------------------------ carpetas

    private async Task<BackupManifest> FoldersAsync(
        string jobFolder, List<(BackupSet Set, BackupManifest Manifest)> sets, Action release, CancellationToken ct, Action<string> setFolderCreated)
    {
        var folders = Job.Elementos.Select(f => f.TrimEnd('\\')).ToList();
        var absent = folders.Where(f => !Directory.Exists(f)).ToList();
        if (absent.Count == folders.Count)
            throw new InvalidOperationException($"No existe ninguna de las carpetas ({string.Join(", ", absent)}).");
        if (absent.Count > 0)
            _warnings.Add($"No existen estas carpetas: {string.Join(", ", absent)}.");
        folders = folders.Except(absent).ToList();

        var previous = sets.LastOrDefault();
        var sameItems = previous.Manifest is not null
            && previous.Manifest.Elementos.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(folders.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var full = BackupCatalog.NeedsFull(sets.Select(s => s.Set).ToList(), Job.Estrategia, Job.CompletoCadaDias, _request.ForzarCompleto, DateTime.UtcNow)
            || !sameItems || previous.Manifest?.Archivos is null;
        _kind = full ? "completo" : "incremental";
        _setName = BackupCatalog.SetName(DateTime.Now, _kind);
        var setFolder = Path.Combine(jobFolder, _setName);
        Directory.CreateDirectory(setFolder);
        setFolderCreated(setFolder);

        using var shadows = new ShadowCopies();
        Progress("Tomando la instantánea de los volúmenes…", force: true);
        foreach (var volume in folders.Select(f => Path.GetPathRoot(f)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (shadows.Create(volume) is { } problem)
                _warnings.Add(problem + " Se copió sin instantánea: los archivos abiertos pueden no copiarse.");
        }
        // Con la foto tomada, en frío los servicios ya pueden volver.
        if (folders.All(f => shadows.Has(Path.GetPathRoot(f)!)))
            release();

        Progress("Revisando los archivos…", force: true);
        var current = new List<ManifestFile>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders)
        {
            var mapped = shadows.Map(folder);
            foreach (var file in new DirectoryInfo(mapped).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                current.Add(new ManifestFile
                {
                    Ruta = folder + file.FullName[mapped.TrimEnd('\\').Length..],
                    Tam = file.Length,
                    Mod = file.LastWriteTimeUtc.Ticks,
                });
            }
        }
        _total = current.Sum(f => f.Tam);
        var (state, toCopy) = BackupCatalog.Diff(current, full ? null : previous.Manifest!.Archivos, _setName);
        var previousByPath = full ? null : previous.Manifest!.Archivos!.ToDictionary(f => f.Ruta, StringComparer.OrdinalIgnoreCase);

        var failed = new List<string>();
        var zipPath = Path.Combine(setFolder, "datos.zip");
        await using (var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in toCopy)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var source = new FileStream(shadows.Map(file.Ruta), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
                    var entry = zip.CreateEntry(BackupCatalog.ZipEntryName(file.Ruta), CompressionLevel.Fastest);
                    entry.LastWriteTime = new DateTimeOffset(new DateTime(file.Mod, DateTimeKind.Utc));
                    await using var target = entry.Open();
                    await CopyStreamAsync(source, target, ct);
                    _files++;
                    Progress($"Copiando… {_files} de {toCopy.Count} archivos");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(file.Ruta);
                    // Lo que no se pudo copiar queda como estaba en el respaldo anterior (o fuera, si es nuevo).
                    var index = state.FindIndex(s => s.Ruta.Equals(file.Ruta, StringComparison.OrdinalIgnoreCase));
                    if (previousByPath?.TryGetValue(file.Ruta, out var old) == true)
                        state[index] = old;
                    else
                        state.RemoveAt(index);
                }
            }
        }
        release();
        if (failed.Count > 0)
            _warnings.Add($"No se pudieron copiar {failed.Count} archivos (por ejemplo {string.Join(", ", failed.Take(3))}).");

        if (Job.Verificar)
        {
            Progress("Verificando la copia…", force: true);
            using var check = ZipFile.OpenRead(zipPath);
            if (check.Entries.Count != _files)
                throw new InvalidOperationException("La verificación del respaldo falló: faltan archivos en la copia.");
            foreach (var entry in check.Entries)
            {
                await using var read = entry.Open();
                await read.CopyToAsync(Stream.Null, ct);
            }
        }

        var depends = state.Select(s => s.En).Append(_setName).Distinct(StringComparer.Ordinal).ToList();
        return new BackupManifest
        {
            TrabajoId = Job.Id, Conjunto = _setName, Fecha = DateTime.UtcNow, Tipo = _kind, Origen = "carpetas",
            Base = full ? null : sets.LastOrDefault(s => s.Set.Kind == "completo").Set?.Name,
            Depende = depends, Elementos = folders, BytesCopiados = _copied, BytesTotales = _total, Archivos = state,
        };
    }

    // ------------------------------------------------------------------ utilidades

    private async Task<long> CopyAsync(string from, string to, CancellationToken ct)
    {
        await using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        await using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        await CopyStreamAsync(source, target, ct);
        return target.Length;
    }

    private async Task CopyStreamAsync(Stream source, Stream target, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            _copied += read;
            Progress($"Copiando… {Size(_copied)}");
        }
    }

    /// <summary>Borra lo que ya no hace falta según la retención; un problema al borrar es solo una advertencia.</summary>
    private void Retention(string jobFolder, string currentSet)
    {
        var sets = BackupCatalog.Read(jobFolder).Select(s => s.Set).ToList();
        foreach (var name in BackupCatalog.ToDelete(sets, Job.RetenerDiarios, Job.RetenerMensuales, TimeZoneInfo.Local))
        {
            if (!TryDeleteDirectory(Path.Combine(jobFolder, name)))
                _warnings.Add($"No se pudo borrar el respaldo viejo {name}.");
        }
        // Respaldos que quedaron a medias (sin manifiesto) de otros días.
        foreach (var dir in Directory.EnumerateDirectories(jobFolder))
        {
            if (Path.GetFileName(dir) != Path.GetFileName(currentSet) && !File.Exists(Path.Combine(dir, BackupCatalog.ManifestName))
                && Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                TryDeleteDirectory(dir);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.#} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

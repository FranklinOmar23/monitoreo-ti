using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MonitoreoTI.AgentHost.Backup;

/// <summary>Un respaldo terminado en el destino (lo que dice su manifiesto).</summary>
public sealed record BackupSet(string Name, DateTime DateUtc, string Kind, string? Base, IReadOnlyList<string> DependsOn);

/// <summary>
/// Cómo quedan los respaldos en el destino (decisión 82) y qué se borra:
/// <c>{destino}\{SERVIDOR}\{id}-{nombre}\{yyyyMMdd-HHmmss}-{tipo}\</c>, cada uno con su manifest.json. Todo se puede
/// restaurar a mano: un .bak por base (el completo y, si hace falta, el diferencial) o el .zip de cada respaldo de
/// carpetas, empezando por su copia completa.
/// </summary>
public static class BackupCatalog
{
    public const string ManifestName = "manifest.json";

    public static string JobFolder(string destinationRoot, string machine, int jobId, string jobName) =>
        Path.Combine(destinationRoot, Safe(machine), $"{jobId}-{Safe(jobName)}");

    public static string SetName(DateTime localTime, string kind) =>
        $"{localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{kind}";

    /// <summary>Un nombre que sirve de carpeta en cualquier destino (sin caracteres raros ni espacios al final).</summary>
    public static string Safe(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Trim())
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : c == ' ' ? '_' : '-');
        var result = builder.ToString().Trim('.', '_', '-');
        return result.Length == 0 ? "respaldo" : result.Length > 60 ? result[..60] : result;
    }

    /// <summary>Los respaldos terminados del trabajo, del más viejo al más nuevo. Los que no tienen manifiesto no cuentan.</summary>
    public static List<(BackupSet Set, BackupManifest Manifest)> Read(string jobFolder)
    {
        var result = new List<(BackupSet, BackupManifest)>();
        if (!Directory.Exists(jobFolder))
            return result;
        foreach (var dir in Directory.EnumerateDirectories(jobFolder))
        {
            var file = Path.Combine(dir, ManifestName);
            if (!File.Exists(file))
                continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file), BackupJson.Options);
                if (manifest is null || manifest.Conjunto != Path.GetFileName(dir))
                    continue;
                result.Add((new BackupSet(manifest.Conjunto, manifest.Fecha, manifest.Tipo, manifest.Base, manifest.Depende), manifest));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Un manifiesto dañado: ese respaldo no se usa de base (la próxima copia será completa si hace falta).
            }
        }
        return result.OrderBy(r => r.Item1.DateUtc).ToList();
    }

    public static void WriteManifest(string setFolder, BackupManifest manifest)
    {
        var temp = Path.Combine(setFolder, ManifestName + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, BackupJson.Options));
        File.Move(temp, Path.Combine(setFolder, ManifestName), overwrite: true);
    }

    /// <summary>
    /// ¿Toca copia completa? Sí si se pidió, si el trabajo es "completo cada vez", si no hay una completa en este destino
    /// (por ejemplo, el otro disco de la rotación) o si la última tiene <paramref name="fullEveryDays"/> días o más.
    /// </summary>
    public static bool NeedsFull(IReadOnlyList<BackupSet> sets, string strategy, int fullEveryDays, bool forced, DateTime nowUtc)
    {
        if (forced || strategy != "incremental")
            return true;
        var lastFull = sets.LastOrDefault(s => s.Kind == "completo");
        return lastFull is null || nowUtc - lastFull.DateUtc >= TimeSpan.FromDays(Math.Max(1, fullEveryDays)) - TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Retención por cadenas: se quedan los últimos <paramref name="keepDaily"/> días con respaldo (el último de cada día)
    /// y el último de cada uno de los últimos <paramref name="keepMonthly"/> meses, más todo aquello de lo que dependen.
    /// Una copia completa no se borra mientras algún respaldo que se queda dependa de ella. El más nuevo siempre se queda.
    /// </summary>
    public static IReadOnlyList<string> ToDelete(IReadOnlyList<BackupSet> sets, int keepDaily, int keepMonthly, TimeZoneInfo zone)
    {
        if (sets.Count == 0)
            return [];
        var ordered = sets.OrderByDescending(s => s.DateUtc).ToList();
        var keep = new HashSet<string> { ordered[0].Name };

        foreach (var day in ordered.GroupBy(s => TimeZoneInfo.ConvertTimeFromUtc(s.DateUtc, zone).Date).Take(Math.Max(1, keepDaily)))
            keep.Add(day.First().Name);
        foreach (var month in ordered.GroupBy(s =>
                 {
                     var local = TimeZoneInfo.ConvertTimeFromUtc(s.DateUtc, zone);
                     return (local.Year, local.Month);
                 }).Take(Math.Max(0, keepMonthly)))
            keep.Add(month.First().Name);

        var byName = sets.ToDictionary(s => s.Name);
        foreach (var name in keep.ToList())
        {
            var set = byName[name];
            foreach (var dependency in set.DependsOn.Append(set.Base).OfType<string>())
                keep.Add(dependency);
        }
        return sets.Where(s => !keep.Contains(s.Name)).Select(s => s.Name).ToList();
    }

    /// <summary>
    /// Archivos que hay que copiar en un incremental: los nuevos o con otro tamaño o fecha que en el estado anterior.
    /// Devuelve el estado nuevo completo (cada archivo con el respaldo que lo tiene) y la lista a copiar.
    /// </summary>
    public static (List<ManifestFile> State, List<ManifestFile> ToCopy) Diff(
        IEnumerable<ManifestFile> current, IReadOnlyList<ManifestFile>? previous, string setName)
    {
        var before = previous?.GroupBy(f => f.Ruta, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var state = new List<ManifestFile>();
        var copy = new List<ManifestFile>();
        foreach (var file in current)
        {
            if (before is not null && before.TryGetValue(file.Ruta, out var old) && old.Tam == file.Tam && old.Mod == file.Mod)
            {
                state.Add(new ManifestFile { Ruta = file.Ruta, Tam = file.Tam, Mod = file.Mod, En = old.En });
                continue;
            }
            var entry = new ManifestFile { Ruta = file.Ruta, Tam = file.Tam, Mod = file.Mod, En = setName };
            state.Add(entry);
            copy.Add(entry);
        }
        return (state, copy);
    }

    /// <summary>Nombre del archivo dentro del .zip: la ruta sin los dos puntos de la unidad (D:\Datos\a.txt → D\Datos\a.txt).</summary>
    public static string ZipEntryName(string path) => path.Replace(":", "", StringComparison.Ordinal).TrimStart('\\').Replace('\\', '/');
}

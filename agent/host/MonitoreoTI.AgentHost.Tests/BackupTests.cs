using System.IO.Compression;
using System.Text.Json;
using MonitoreoTI.AgentHost.Backup;
using Xunit;

namespace MonitoreoTI.AgentHost.Tests;

/// <summary>Respaldos de servidores (decisión 82): la cadena de copias, la retención y un respaldo real de carpetas.</summary>
public sealed class BackupCatalogTests
{
    private static readonly DateTime Day = new(2026, 11, 1, 23, 0, 0, DateTimeKind.Utc);

    private static BackupSet Full(int day) => new($"d{day}-completo", Day.AddDays(day), "completo", null, [$"d{day}-completo"]);

    private static BackupSet Diff(int day, int baseDay) =>
        new($"d{day}-diferencial", Day.AddDays(day), "diferencial", $"d{baseDay}-completo", [$"d{baseDay}-completo", $"d{day}-diferencial"]);

    [Fact]
    public void Una_copia_completa_no_se_borra_mientras_un_diferencial_que_se_queda_la_necesite()
    {
        // Completo el día 0, diferenciales del 1 al 6, completo el 7 y diferenciales el 8 y 9. Se guardan 3 diarios.
        var sets = new List<BackupSet> { Full(0) };
        sets.AddRange(Enumerable.Range(1, 6).Select(d => Diff(d, 0)));
        sets.Add(Full(7));
        sets.AddRange([Diff(8, 7), Diff(9, 7)]);

        var deleted = BackupCatalog.ToDelete(sets, keepDaily: 3, keepMonthly: 0, TimeZoneInfo.Utc);

        // Quedan 7, 8 y 9: todo lo de la cadena vieja se borra.
        Assert.Equal(sets.Take(7).Select(s => s.Name), deleted);
    }

    [Fact]
    public void Si_un_diferencial_que_se_queda_depende_de_la_completa_vieja_esta_tambien_se_queda()
    {
        var sets = new List<BackupSet> { Full(0), Diff(1, 0), Diff(2, 0), Diff(3, 0) };
        var deleted = BackupCatalog.ToDelete(sets, keepDaily: 2, keepMonthly: 0, TimeZoneInfo.Utc);
        Assert.Equal(["d1-diferencial"], deleted);
    }

    [Fact]
    public void Los_mensuales_guardan_el_ultimo_de_cada_mes()
    {
        var sets = new List<BackupSet>
        {
            new("oct-a", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), "completo", null, ["oct-a"]),
            new("oct-b", new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc), "completo", null, ["oct-b"]),
            new("nov-a", new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc), "completo", null, ["nov-a"]),
        };
        Assert.Equal(["oct-a"], BackupCatalog.ToDelete(sets, keepDaily: 1, keepMonthly: 2, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("completo", 7, false, 1, true)] // completo cada vez
    [InlineData("incremental", 7, false, 3, false)]
    [InlineData("incremental", 7, false, 7, true)] // ya toca la completa de la semana
    [InlineData("incremental", 7, true, 1, true)] // "Hacer completo ahora"
    public void Cuando_toca_copia_completa(string strategy, int every, bool forced, int daysSinceFull, bool expected) =>
        Assert.Equal(expected, BackupCatalog.NeedsFull([Full(0)], strategy, every, forced, Day.AddDays(daysSinceFull)));

    [Fact]
    public void Sin_copia_completa_en_este_disco_la_proxima_es_completa() =>
        Assert.True(BackupCatalog.NeedsFull([], "incremental", 7, false, Day));

    [Fact]
    public void El_incremental_copia_solo_lo_nuevo_o_cambiado()
    {
        var previous = new List<ManifestFile>
        {
            new() { Ruta = @"D:\Datos\a.txt", Tam = 10, Mod = 1, En = "base" },
            new() { Ruta = @"D:\Datos\b.txt", Tam = 10, Mod = 1, En = "base" },
            new() { Ruta = @"D:\Datos\borrado.txt", Tam = 1, Mod = 1, En = "base" },
        };
        var current = new List<ManifestFile>
        {
            new() { Ruta = @"D:\Datos\a.txt", Tam = 10, Mod = 1 },
            new() { Ruta = @"D:\Datos\b.txt", Tam = 12, Mod = 2 },
            new() { Ruta = @"D:\Datos\nuevo.txt", Tam = 5, Mod = 3 },
        };
        var (state, copy) = BackupCatalog.Diff(current, previous, "hoy");
        Assert.Equal([@"D:\Datos\b.txt", @"D:\Datos\nuevo.txt"], copy.Select(c => c.Ruta));
        Assert.Equal(["base", "hoy", "hoy"], state.Select(s => s.En));
        Assert.DoesNotContain(state, s => s.Ruta.EndsWith("borrado.txt"));
    }

    [Theory]
    [InlineData("ERP Caribe", "ERP_Caribe")]
    [InlineData("..\\otra", "otra")]
    [InlineData("a/b:c", "a-b-c")]
    public void Los_nombres_de_carpeta_son_seguros(string name, string expected) => Assert.Equal(expected, BackupCatalog.Safe(name));
}

/// <summary>Un respaldo real de carpetas a un destino local: primero completo, después solo lo nuevo.</summary>
public sealed class FolderBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "centinela-respaldo-" + Guid.NewGuid().ToString("N")[..8]);
    private string Source => Path.Combine(_root, "datos");
    private string Target => Path.Combine(_root, "destino");

    public FolderBackupTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "sub"));
        File.WriteAllText(Path.Combine(Source, "a.txt"), "uno");
        File.WriteAllText(Path.Combine(Source, "sub", "b.txt"), "dos");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(BackupStep Final, List<BackupStep> Steps)> RunAsync(bool forceFull = false)
    {
        var request = new BackupRequest
        {
            Ejecucion = Guid.NewGuid().ToString(),
            ForzarCompleto = forceFull,
            Equipo = "SRV-PRUEBA",
            Trabajo = new BackupJob
            {
                Id = 3, Nombre = "Datos", Origen = "carpetas", Elementos = [Source], Estrategia = "incremental", CompletoCadaDias = 7,
                Destino = new BackupDestination { Tipo = "local", Ruta = Target }, RetenerDiarios = 7, RetenerMensuales = 0,
            },
        };
        using var output = new StringWriter();
        await BackupRunner.RunAsync(new StringReader(JsonSerializer.Serialize(request, BackupJson.Options)), output);
        var steps = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonSerializer.Deserialize<BackupStep>(l, BackupJson.Options)!).ToList();
        return (steps.Single(s => s.Fin == true), steps);
    }

    private string JobFolder => BackupCatalog.JobFolder(Target, "SRV-PRUEBA", 3, "Datos");

    [Fact]
    public async Task Primero_completo_y_despues_solo_lo_nuevo()
    {
        var (first, _) = await RunAsync();
        // Sin permisos de administrador no hay instantánea: termina con advertencia, pero copia todo.
        Assert.NotEqual("error", first.Estado);
        Assert.Equal("completo", first.Tipo);
        Assert.Equal(2, first.Archivos);

        await Task.Delay(1100); // otro nombre de respaldo (van por segundo)
        File.WriteAllText(Path.Combine(Source, "a.txt"), "uno cambiado");
        File.WriteAllText(Path.Combine(Source, "c.txt"), "nuevo");
        var (second, _) = await RunAsync();
        Assert.NotEqual("error", second.Estado);
        Assert.Equal("incremental", second.Tipo);
        Assert.Equal(2, second.Archivos); // a.txt cambiado y c.txt nuevo; b.txt no se vuelve a copiar

        var sets = BackupCatalog.Read(JobFolder);
        Assert.Equal(["completo", "incremental"], sets.Select(s => s.Set.Kind));
        var last = sets[1].Manifest;
        Assert.Equal(3, last.Archivos!.Count);
        Assert.Equal(sets[0].Set.Name, last.Archivos.Single(f => f.Ruta.EndsWith("b.txt")).En);
        Assert.Contains(sets[0].Set.Name, last.Depende);

        using var zip = ZipFile.OpenRead(Path.Combine(JobFolder, sets[1].Set.Name, "datos.zip"));
        Assert.Equal(2, zip.Entries.Count);
        Assert.Contains(zip.Entries, e => e.FullName.EndsWith("datos/c.txt"));
    }

    [Fact]
    public async Task Hacer_completo_ahora_copia_todo_aunque_haya_base()
    {
        await RunAsync();
        await Task.Delay(1100);
        var (forced, _) = await RunAsync(forceFull: true);
        Assert.Equal("completo", forced.Tipo);
        Assert.Equal(2, forced.Archivos);
    }

    [Fact]
    public async Task Un_destino_que_no_existe_ni_se_puede_crear_es_un_error_claro()
    {
        var request = new BackupRequest
        {
            Ejecucion = Guid.NewGuid().ToString(),
            Trabajo = new BackupJob
            {
                Id = 4, Nombre = "X", Origen = "carpetas", Elementos = [Source],
                Destino = new BackupDestination { Tipo = "disco_externo", Discos = [new BackupDiskRef { Serie = "NO-EXISTE", Nombre = "WD Elements" }] },
            },
        };
        using var output = new StringWriter();
        await BackupRunner.RunAsync(new StringReader(JsonSerializer.Serialize(request, BackupJson.Options)), output);
        var final = JsonSerializer.Deserialize<BackupStep>(output.ToString().Trim().Split('\n')[^1], BackupJson.Options)!;
        Assert.Equal("error", final.Estado);
        Assert.Contains("WD Elements", final.Detalle);
    }
}

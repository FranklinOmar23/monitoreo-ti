using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Backups;

/// <summary>Respaldos de servidores (decisión 82): valores fijos, alertas y firma de lo que se manda al agente.</summary>
public static class BackupRules
{
    /// <summary>Primera versión del agente que hace respaldos.</summary>
    public static readonly Version AgentVersion = new(0, 4, 0);

    public static bool SupportsBackups(string? agentVersion) =>
        Version.TryParse(agentVersion?.Split('-', '+')[0], out var version) && version >= AgentVersion
        && agentVersion?.Contains("clasico", StringComparison.OrdinalIgnoreCase) != true;

    public const string Sql = "sql";
    public const string Folders = "carpetas";
    public const string Hot = "caliente";
    public const string Cold = "frio";
    public const string Full = "completo";
    public const string Incremental = "incremental";
    public const string ExternalDisk = "disco_externo";
    public const string Network = "red";
    public const string Local = "local";
    public const string Daily = "diario";
    public const string Weekly = "semanal";

    public const string Running = "en_curso";
    public const string Ok = "correcto";
    public const string Warning = "advertencia";
    public const string Failed = "error";

    public static readonly IReadOnlySet<string> Statuses = new HashSet<string> { Running, Ok, Warning, Failed };
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "completo", "incremental", "diferencial" };

    /// <summary>Margen sobre el horario antes de dar un respaldo por atrasado (un respaldo grande tarda).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(2);

    /// <summary>Una ejecución "en curso" sin noticias por tanto tiempo se da por perdida (el agente se cayó).</summary>
    public static readonly TimeSpan RunningLost = TimeSpan.FromHours(12);

    /// <summary>"Respaldar ahora" vence si el agente no lo recibe en este tiempo.</summary>
    public static readonly TimeSpan RunNowLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Días de la semana elegidos (0 = domingo); vacío si no es semanal o no hay.</summary>
    public static IReadOnlyList<int> ParseWeekDays(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => int.TryParse(d, out var day) ? day : -1).Where(d => d is >= 0 and <= 6).Distinct().Order().ToList();

    /// <summary>
    /// El mayor tiempo que puede pasar entre dos respaldos según el horario, más el margen: un día para los diarios; para
    /// los semanales, el hueco más largo entre los días elegidos.
    /// </summary>
    public static TimeSpan MaxGap(string frequency, string? weekDays)
    {
        if (frequency != Weekly)
            return TimeSpan.FromDays(1) + Grace;
        var days = ParseWeekDays(weekDays);
        if (days.Count == 0)
            return TimeSpan.FromDays(7) + Grace;
        var gap = 0;
        for (var i = 0; i < days.Count; i++)
        {
            var next = i + 1 < days.Count ? days[i + 1] : days[0] + 7;
            gap = Math.Max(gap, next - days[i]);
        }
        return TimeSpan.FromDays(gap) + Grace;
    }

    /// <summary>Alertas de los respaldos de un equipo: fallido, atrasado, sin disco y servicios que no arrancaron.</summary>
    public static IReadOnlyList<Finding> Evaluate(IEnumerable<BackupJobSnapshot> jobs, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        foreach (var job in jobs.Where(j => j.Active))
        {
            if (!string.IsNullOrWhiteSpace(job.ServicesNotRestarted))
                findings.Add(new Finding($"respaldo_servicios:{job.Id}", FindingLevel.Critical,
                    $"Servicios detenidos por el respaldo «{job.Name}»",
                    $"No volvieron a arrancar después del respaldo en frío: {job.ServicesNotRestarted}.",
                    "Inícielos desde la ficha del equipo (Servicios) o en el servidor. Revise por qué no arrancan antes del próximo respaldo.",
                    "Respaldos", null));

            if (job.LastStatus == Failed)
                findings.Add(new Finding($"respaldo_fallido:{job.Id}", FindingLevel.Critical,
                    $"Falló el respaldo «{job.Name}»",
                    job.LastError ?? "El último respaldo terminó con error.",
                    "Revise el detalle en la pestaña Respaldos del servidor y use «Respaldar ahora» cuando esté resuelto.",
                    "Respaldos", null));
            else if (job.LastStatus == Warning)
                findings.Add(new Finding($"respaldo_advertencia:{job.Id}", FindingLevel.Warning,
                    $"El respaldo «{job.Name}» terminó con advertencias",
                    job.LastError ?? "El respaldo se hizo, pero con advertencias.",
                    "Revise el detalle en la pestaña Respaldos del servidor.",
                    "Respaldos", null));

            var since = job.LastSuccessAt ?? job.CreatedAt;
            if (job.LastStatus != Failed && nowUtc - since > MaxGap(job.Frequency, job.WeekDays))
                findings.Add(new Finding($"respaldo_atrasado:{job.Id}", FindingLevel.Critical,
                    $"Sin respaldo reciente: «{job.Name}»",
                    job.LastSuccessAt is null
                        ? "Todavía no se ha hecho ningún respaldo correcto con este trabajo."
                        : $"El último respaldo correcto fue el {job.LastSuccessAt:dd/MM/yyyy HH:mm} (UTC).",
                    "Revise que el agente del servidor esté conectado y que el destino esté disponible; puede usar «Respaldar ahora».",
                    "Respaldos", null));

            if (job.DiskMissingSince is not null)
                findings.Add(new Finding($"respaldo_sin_disco:{job.Id}", FindingLevel.Warning,
                    $"Conecte el disco del respaldo «{job.Name}»",
                    "Ninguno de los discos externos de este respaldo está conectado al servidor.",
                    "Conecte uno de los discos elegidos antes de la hora del respaldo.",
                    "Respaldos", null));
        }
        return findings;
    }

    /// <summary>Texto firmado de la configuración de respaldos de un equipo (el agente la verifica con la llave de la empresa).</summary>
    public static string ConfigPayload(int machineId, string configJson) =>
        string.Join('\n', "monitoreoti/respaldos/v1", machineId.ToString(), Sha256(configJson));

    /// <summary>Texto firmado de "Respaldar ahora".</summary>
    public static string RunNowPayload(int machineId, int jobId, string runUid, bool full, string expires) =>
        string.Join('\n', "monitoreoti/respaldo-ahora/v1", machineId.ToString(), jobId.ToString(), runUid, full ? "completo" : "normal", expires);

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>Lo que necesita el motor de alertas de cada trabajo.</summary>
public sealed record BackupJobSnapshot(
    int Id, string Name, bool Active, string Frequency, string? WeekDays, DateTime CreatedAt, string? LastStatus,
    DateTime? LastSuccessAt, string? LastError, DateTime? DiskMissingSince, string? ServicesNotRestarted);

// ------------------------------------------------------------------ panel

public sealed record BackupDisk(string Serie, string Nombre);

public sealed record BackupJobInput(
    string Nombre,
    string Origen,
    string? InstanciaSql,
    IReadOnlyList<string> Elementos,
    string Modo,
    IReadOnlyList<string>? ServiciosDetener,
    int FrioMaxMinutos,
    string Estrategia,
    int CompletoCadaDias,
    string DestinoTipo,
    string? DestinoRuta,
    IReadOnlyList<BackupDisk>? Discos,
    bool Expulsar,
    string? RedUsuario,
    string? RedClave,
    string Frecuencia,
    IReadOnlyList<int>? DiasSemana,
    string Hora,
    int RetenerDiarios,
    int RetenerMensuales,
    bool Verificar,
    bool Activo);

public sealed partial class BackupJobInputValidator : AbstractValidator<BackupJobInput>
{
    [GeneratedRegex(@"^[A-Za-z0-9_$\-]{1,128}$")]
    private static partial Regex InstanceName();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex LocalPath();

    [GeneratedRegex(@"^\\\\[^\\/:*?""<>|]+\\[^\\/:*?""<>|]+")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"^[A-Za-z0-9_.$\- ]{1,256}$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex Time();

    public static bool IsLocalFolder(string? path) =>
        path is { Length: >= 3 and <= 260 } && LocalPath().IsMatch(path) && !path.Contains("..") && path.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0;

    public static bool IsNetworkFolder(string? path) =>
        path is { Length: <= 260 } && UncPath().IsMatch(path) && !path.Contains("..");

    /// <summary>La carpeta dentro del disco externo: relativa, sin unidad ni "..".</summary>
    public static bool IsDiskFolder(string? path) =>
        string.IsNullOrEmpty(path) || (path.Length <= 120 && !path.Contains("..") && !path.Contains(':') && !path.StartsWith('\\')
            && path.IndexOfAny(['*', '?', '"', '<', '>', '|', '/']) < 0);

    public BackupJobInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().MaximumLength(100).WithMessage("Escriba un nombre de hasta 100 caracteres.");
        RuleFor(x => x.Origen).Must(o => o is BackupRules.Sql or BackupRules.Folders).WithMessage("Elija qué respaldar.");
        RuleFor(x => x.InstanciaSql).Must(i => i is not null && InstanceName().IsMatch(i))
            .When(x => x.Origen == BackupRules.Sql).WithMessage("Elija la instancia de SQL Server.");
        RuleFor(x => x.Elementos).Must(e => e is { Count: > 0 and <= 50 }).WithMessage("Elija al menos una base o carpeta (hasta 50).");
        RuleForEach(x => x.Elementos).Must(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 128 && !n.Contains(']'))
            .When(x => x.Origen == BackupRules.Sql).WithMessage("Nombre de base no válido.");
        RuleForEach(x => x.Elementos).Must(IsLocalFolder)
            .When(x => x.Origen == BackupRules.Folders).WithMessage("Las carpetas van con su ruta completa en el servidor (por ejemplo D:\\Datos).");
        RuleFor(x => x.Modo).Must(m => m is BackupRules.Hot or BackupRules.Cold).WithMessage("Elija en caliente o en frío.");
        RuleFor(x => x.ServiciosDetener).Must(s => s is { Count: > 0 and <= 20 })
            .When(x => x.Modo == BackupRules.Cold).WithMessage("En frío, elija los servicios que se detienen (hasta 20).");
        RuleForEach(x => x.ServiciosDetener).Must(s => s is not null && ServiceName().IsMatch(s)).WithMessage("Nombre de servicio no válido.");
        RuleFor(x => x.FrioMaxMinutos).InclusiveBetween(5, 720).When(x => x.Modo == BackupRules.Cold)
            .WithMessage("El tiempo máximo sin servicio va de 5 a 720 minutos.");
        RuleFor(x => x.Estrategia).Must(s => s is BackupRules.Full or BackupRules.Incremental).WithMessage("Elija completo o solo lo nuevo.");
        RuleFor(x => x.CompletoCadaDias).InclusiveBetween(1, 60).When(x => x.Estrategia == BackupRules.Incremental)
            .WithMessage("La copia completa va cada 1 a 60 días.");
        RuleFor(x => x.DestinoTipo).Must(d => d is BackupRules.ExternalDisk or BackupRules.Network or BackupRules.Local)
            .WithMessage("Elija dónde guardar el respaldo.");
        RuleFor(x => x.DestinoRuta).Must(IsNetworkFolder).When(x => x.DestinoTipo == BackupRules.Network)
            .WithMessage("La carpeta de red va como \\\\servidor\\carpeta.");
        RuleFor(x => x.DestinoRuta).Must(IsLocalFolder).When(x => x.DestinoTipo == BackupRules.Local)
            .WithMessage("La carpeta local va con su ruta completa (por ejemplo E:\\Respaldos).");
        RuleFor(x => x.DestinoRuta).Must(IsDiskFolder).When(x => x.DestinoTipo == BackupRules.ExternalDisk)
            .WithMessage("La carpeta dentro del disco es un nombre simple (por ejemplo Centinela).");
        RuleFor(x => x.Discos).Must(d => d is { Count: > 0 and <= 4 }).When(x => x.DestinoTipo == BackupRules.ExternalDisk)
            .WithMessage("Elija de uno a cuatro discos externos.");
        RuleForEach(x => x.Discos).Must(d => d is not null && !string.IsNullOrWhiteSpace(d.Serie) && d.Serie.Length <= 100
            && !string.IsNullOrWhiteSpace(d.Nombre) && d.Nombre.Length <= 100).WithMessage("Disco no válido.");
        RuleFor(x => x.RedUsuario).MaximumLength(150);
        RuleFor(x => x.RedClave).MaximumLength(256);
        RuleFor(x => x.Frecuencia).Must(f => f is BackupRules.Daily or BackupRules.Weekly).WithMessage("Elija diario o semanal.");
        RuleFor(x => x.DiasSemana).Must(d => d is { Count: > 0 } && d.All(day => day is >= 0 and <= 6))
            .When(x => x.Frecuencia == BackupRules.Weekly).WithMessage("Elija los días de la semana.");
        RuleFor(x => x.Hora).Must(h => h is not null && Time().IsMatch(h)).WithMessage("La hora va como HH:mm.");
        RuleFor(x => x.RetenerDiarios).InclusiveBetween(1, 90).WithMessage("Se guardan de 1 a 90 respaldos diarios.");
        RuleFor(x => x.RetenerMensuales).InclusiveBetween(0, 36).WithMessage("Se guardan de 0 a 36 respaldos mensuales.");
    }
}

/// <summary>Probar el destino antes de guardar: con la clave escrita o, si no se escribió, la del trabajo.</summary>
public sealed record BackupDestinationInput(
    string DestinoTipo, string? DestinoRuta, IReadOnlyList<BackupDisk>? Discos, string? RedUsuario, string? RedClave, int? TrabajoId);

public sealed class BackupDestinationInputValidator : AbstractValidator<BackupDestinationInput>
{
    public BackupDestinationInputValidator()
    {
        RuleFor(x => x.DestinoTipo).Must(d => d is BackupRules.ExternalDisk or BackupRules.Network or BackupRules.Local)
            .WithMessage("Elija dónde guardar el respaldo.");
        RuleFor(x => x.DestinoRuta).Must(BackupJobInputValidator.IsNetworkFolder).When(x => x.DestinoTipo == BackupRules.Network)
            .WithMessage("La carpeta de red va como \\\\servidor\\carpeta.");
        RuleFor(x => x.DestinoRuta).Must(BackupJobInputValidator.IsLocalFolder).When(x => x.DestinoTipo == BackupRules.Local)
            .WithMessage("La carpeta local va con su ruta completa (por ejemplo E:\\Respaldos).");
        RuleFor(x => x.Discos).Must(d => d is { Count: > 0 and <= 4 }).When(x => x.DestinoTipo == BackupRules.ExternalDisk)
            .WithMessage("Elija de uno a cuatro discos externos.");
        RuleFor(x => x.RedUsuario).MaximumLength(150);
        RuleFor(x => x.RedClave).MaximumLength(256);
    }
}

public sealed record BackupRunNowInput(bool Completo);

public sealed record BackupJobItem(
    int Id, string Name, string Source, string? SqlInstance, IReadOnlyList<string> Items, string Mode,
    IReadOnlyList<string> StopServices, int ColdMaxMinutes, string Strategy, int FullEveryDays, string DestinationType,
    string DestinationPath, IReadOnlyList<BackupDisk> Disks, bool EjectAfter, string? NetworkUser, bool HasNetworkPassword,
    string Frequency, IReadOnlyList<int> WeekDays, string Time, int KeepDaily, int KeepMonthly, bool Verify, bool IsActive,
    DateTime? LastRunAt, string? LastStatus, DateTime? LastSuccessAt, string? LastError, DateTime? NextRunAt,
    DateTime? DiskMissingSince, string? ServicesNotRestarted, DateTime CreatedAt);

public sealed record BackupRunItem(
    long Id, int JobId, string JobName, string Trigger, string? RequestedBy, string Status, string? Kind, DateTime StartedAt,
    DateTime? FinishedAt, long BytesCopied, long BytesTotal, int? Files, string? Destination, string? SetName, string? Detail);

public sealed record BackupOverview(
    int MachineId, string MachineName, bool IsServer, bool TypeLocked, string? AgentVersion, bool AgentConnected,
    bool AgentSupportsBackups, int OkLast30Days, int FailedLast30Days, IReadOnlyList<BackupJobItem> Jobs, IReadOnlyList<BackupRunItem> Runs);

public sealed record SqlDatabaseInfo(string Name, long SizeMb, string? RecoveryModel);
public sealed record SqlInstanceInfo(string Name, string? Error, IReadOnlyList<SqlDatabaseInfo> Databases);
public sealed record ServiceInfo(string Name, string DisplayName, string State);
public sealed record ExternalDiskInfo(string Serial, string Name, string? Letter, double? TotalGb, double? FreeGb);
public sealed record BackupExploreResult(
    bool Ok, string? Error, IReadOnlyList<SqlInstanceInfo> Instances, IReadOnlyList<ServiceInfo> Services, IReadOnlyList<ExternalDiskInfo> Disks);
public sealed record BackupDestinationResult(bool Ok, string? Error, double? FreeGb, string? Location);

// ------------------------------------------------------------------ agente → servidor

public sealed record BaseSqlData(string Nombre, long TamanoMb, string? Recuperacion);
public sealed record InstanciaSqlData(string Nombre, string? Error, List<BaseSqlData>? Bases);
public sealed record ServicioData(string Nombre, string? Mostrar, string? Estado);
public sealed record DiscoExternoData(string Serie, string? Nombre, string? Letra, double? TotalGb, double? LibreGb);

public sealed record RespaldoExploradoData(
    string SolicitudId, bool Ok, string? Error, List<InstanciaSqlData>? Instancias, List<ServicioData>? Servicios, List<DiscoExternoData>? Discos);

public sealed record RespaldoPruebaData(string SolicitudId, bool Ok, string? Error, double? LibreGb, string? Ubicacion);

/// <summary>Inicio, avance o final de una ejecución.</summary>
public sealed record RespaldoEstadoData(
    int TrabajoId, string Ejecucion, string Estado, string? Tipo, string? Disparo, DateTime Inicio, DateTime? Fin,
    long BytesCopiados, long BytesTotales, int? Archivos, string? Destino, string? Conjunto, string? Detalle,
    List<string>? ServiciosSinArrancar);

public sealed record AgendaRespaldoData(int TrabajoId, DateTime? Proxima, bool? DiscoConectado);

/// <summary>El agente informa la próxima ejecución de cada trabajo y si tiene disco (al recibir la configuración y cada hora).</summary>
public sealed record RespaldosAgendaData(List<AgendaRespaldoData>? Trabajos);

/// <summary>Lado del gateway: manda la configuración y los pedidos al agente del servidor.</summary>
public interface IBackupAgentLink
{
    bool IsConnected(int companyId, int agentId);

    /// <summary>La configuración firmada del equipo (al cambiar un trabajo); false si el agente no está conectado.</summary>
    Task<bool> PublishAsync(int companyId, int agentId, CancellationToken cancellationToken);

    Task<bool> RunNowAsync(int companyId, int agentId, object signedOrder, CancellationToken cancellationToken);
    Task<RespaldoExploradoData?> ExploreAsync(int companyId, int agentId, CancellationToken cancellationToken);
    Task<RespaldoPruebaData?> TestDestinationAsync(int companyId, int agentId, object destination, CancellationToken cancellationToken);
}

/// <summary>Lo que informa el agente sobre sus respaldos.</summary>
public interface IBackupStatusStore
{
    Task SaveRunAsync(AgentIdentity identity, RespaldoEstadoData data, DateTime receivedAtUtc, CancellationToken cancellationToken);
    Task SaveScheduleAsync(AgentIdentity identity, RespaldosAgendaData data, DateTime receivedAtUtc, CancellationToken cancellationToken);

    /// <summary>La configuración firmada que le toca al agente; null si su equipo no tiene trabajos.</summary>
    Task<object?> SignedConfigAsync(AgentIdentity identity, CancellationToken cancellationToken);
}

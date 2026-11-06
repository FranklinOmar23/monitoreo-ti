using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Backups;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Backups;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Health;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Backups;

/// <summary>
/// Respaldos de servidores desde el panel (decisión 82): los trabajos de cada servidor, su historial, "Respaldar ahora"
/// y lo que se le pregunta al agente (bases, servicios y discos; probar el destino). La clave de la carpeta de red se
/// guarda cifrada y solo viaja sellada para el agente de ese servidor; nunca vuelve al panel ni va a la auditoría.
/// </summary>
public sealed class Backups(
    MonitoreoDbContext db, PanelQueries queries, IAuditTrail audit, ICurrentUser user, IBackupAgentLink link,
    ICompanySigner signer, SecretProtector protector, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Configurar o lanzar respaldos detiene servicios y saca datos del servidor: pide un 2FA reciente (decisión 82).</summary>
    public async Task<bool> RecentTwoFactorAsync(CancellationToken ct)
    {
        var userId = user.UserId;
        var last = await db.Users.Where(u => u.Id == userId).Select(u => u.LastTwoFactorAt).FirstOrDefaultAsync(ct);
        return last is not null && last >= Now - RemoteRules.TwoFactorWindow;
    }

    private sealed record MachineInfo(int Id, string Name, MachineType Type, bool TypeLocked, int? AgentId, string? AgentVersion, string? TokenHash);

    private async Task<MachineInfo?> MachineAsync(int machineId, CancellationToken ct)
    {
        if (!await queries.CanSeeMachineAsync(machineId, ct))
            return null;
        return await db.Machines.AsNoTracking().Where(m => m.Id == machineId && m.Status != MachineStatus.Retired)
            .Select(m => new MachineInfo(m.Id, m.Name, m.Type, m.TypeLocked,
                m.Agent != null && !m.Agent.IsRevoked ? m.Agent.Id : null,
                m.Agent != null ? m.Agent.Version : null,
                m.Agent != null ? m.Agent.TokenHash : null))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<BackupOverview?> OverviewAsync(int machineId, CancellationToken ct)
    {
        var machine = await MachineAsync(machineId, ct);
        if (machine is null)
            return null;
        var now = Now;
        var jobs = await db.BackupJobs.AsNoTracking().Where(j => j.MachineId == machineId).OrderBy(j => j.Name).ToListAsync(ct);
        var since = now.AddDays(-30);
        var runs = await db.BackupRuns.AsNoTracking()
            .Where(r => r.MachineId == machineId && r.StartedAt >= since)
            .OrderByDescending(r => r.StartedAt).Take(200)
            .Select(r => new { Run = r, RequestedBy = db.Users.Where(u => u.Id == r.RequestedById).Select(u => u.Name).FirstOrDefault() })
            .ToListAsync(ct);
        var names = jobs.ToDictionary(j => j.Id, j => j.Name);
        var items = runs.Select(x => ToItem(x.Run, names.GetValueOrDefault(x.Run.JobId, "?"), x.RequestedBy, now)).ToList();

        return new BackupOverview(
            machine.Id, machine.Name, machine.Type == MachineType.Server, machine.TypeLocked, machine.AgentVersion,
            machine.AgentId is int agentId && link.IsConnected(user.CompanyId, agentId),
            BackupRules.SupportsBackups(machine.AgentVersion),
            items.Count(r => r.Status is BackupRules.Ok or BackupRules.Warning),
            items.Count(r => r.Status == BackupRules.Failed),
            jobs.Select(ToItem).ToList(),
            items);
    }

    public async Task<CommandResult> SaveAsync(int machineId, int? jobId, BackupJobInput input, CancellationToken ct)
    {
        var machine = await MachineAsync(machineId, ct);
        if (machine is null)
            return CommandResult.NotFound();
        if (machine.Type != MachineType.Server)
            return CommandResult.Invalid("Los respaldos son para servidores: marque el equipo como servidor en Editar.");
        if (machine.AgentId is null)
            return CommandResult.Invalid("El equipo no tiene agente: los respaldos los hace el agente del servidor.");
        if (!BackupRules.SupportsBackups(machine.AgentVersion))
            return CommandResult.Conflict(
                $"El agente de {machine.Name} es la versión {machine.AgentVersion ?? "?"}; los respaldos necesitan la {BackupRules.AgentVersion} o superior.");

        BackupJob job;
        object? before = null;
        if (jobId is int id)
        {
            var found = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == id && j.MachineId == machineId, ct);
            if (found is null)
                return CommandResult.NotFound();
            job = found;
            before = AuditView(job);
        }
        else
        {
            job = new BackupJob { MachineId = machineId, CreatedById = user.UserId, CreatedAt = Now };
            db.BackupJobs.Add(job);
        }

        if (input.DestinoTipo == BackupRules.Network && input.RedClave is null && job.NetworkEncryptedPassword is null
            && !string.IsNullOrWhiteSpace(input.RedUsuario))
            return CommandResult.Invalid("Escriba la clave de la cuenta de la carpeta de red.");

        job.Name = input.Nombre.Trim();
        job.Source = input.Origen;
        job.SqlInstance = input.Origen == BackupRules.Sql ? input.InstanciaSql : null;
        job.Items = JsonSerializer.Serialize(input.Elementos.Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        job.Mode = input.Modo;
        job.StopServices = JsonSerializer.Serialize(input.Modo == BackupRules.Cold ? (input.ServiciosDetener ?? []) : []);
        job.ColdMaxMinutes = input.Modo == BackupRules.Cold ? input.FrioMaxMinutos : 60;
        job.Strategy = input.Estrategia;
        job.FullEveryDays = input.Estrategia == BackupRules.Incremental ? input.CompletoCadaDias : 1;
        job.DestinationType = input.DestinoTipo;
        job.DestinationPath = input.DestinoTipo == BackupRules.ExternalDisk
            ? (string.IsNullOrWhiteSpace(input.DestinoRuta) ? "Centinela" : input.DestinoRuta.Trim())
            : input.DestinoRuta!.Trim().TrimEnd('\\');
        job.Disks = JsonSerializer.Serialize(input.DestinoTipo == BackupRules.ExternalDisk ? (input.Discos ?? []) : []);
        job.EjectAfter = input.DestinoTipo == BackupRules.ExternalDisk && input.Expulsar;
        if (input.DestinoTipo == BackupRules.Network && !string.IsNullOrWhiteSpace(input.RedUsuario))
        {
            job.NetworkUser = input.RedUsuario.Trim();
            // null = se conserva la clave guardada; texto = se reemplaza.
            if (!string.IsNullOrEmpty(input.RedClave))
                job.NetworkEncryptedPassword = Encoding.UTF8.GetBytes(protector.Protect(input.RedClave));
        }
        else
        {
            job.NetworkUser = null;
            job.NetworkEncryptedPassword = null;
        }
        job.Frequency = input.Frecuencia;
        job.WeekDays = input.Frecuencia == BackupRules.Weekly ? string.Join(',', input.DiasSemana!.Distinct().Order()) : null;
        job.Time = input.Hora;
        job.KeepDaily = input.RetenerDiarios;
        job.KeepMonthly = input.RetenerMensuales;
        job.Verify = input.Verificar;
        job.IsActive = input.Activo;
        job.UpdatedAt = Now;
        if (!job.IsActive)
            job.DiskMissingSince = null;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(jobId is null ? "respaldo.crear" : "respaldo.editar", "respaldo_trabajos", job.Id.ToString(),
            before, AuditView(job), ct);
        await link.PublishAsync(user.CompanyId, machine.AgentId.Value, ct);
        return CommandResult.Ok(job.Id);
    }

    public async Task<CommandResult> DeleteAsync(int machineId, int jobId, CancellationToken ct)
    {
        var machine = await MachineAsync(machineId, ct);
        var job = machine is null ? null : await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.MachineId == machineId, ct);
        if (machine is null || job is null)
            return CommandResult.NotFound();
        var before = AuditView(job);
        db.BackupJobs.Remove(job);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("respaldo.eliminar", "respaldo_trabajos", jobId.ToString(), before, null, ct);
        if (machine.AgentId is int agentId)
            await link.PublishAsync(user.CompanyId, agentId, ct);
        return CommandResult.Ok(jobId);
    }

    /// <summary>"Respaldar ahora" (o "Hacer completo ahora"): orden firmada para el agente, con su vencimiento.</summary>
    public async Task<CommandResult> RunNowAsync(int machineId, int jobId, bool full, CancellationToken ct)
    {
        var machine = await MachineAsync(machineId, ct);
        var job = machine is null ? null : await db.BackupJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId && j.MachineId == machineId, ct);
        if (machine is null || job is null)
            return CommandResult.NotFound();
        if (machine.AgentId is not int agentId || !link.IsConnected(user.CompanyId, agentId))
            return CommandResult.Conflict($"El agente de {machine.Name} no está conectado.");
        if (!BackupRules.SupportsBackups(machine.AgentVersion))
            return CommandResult.Conflict($"Los respaldos necesitan el agente {BackupRules.AgentVersion} o superior.");
        var lost = Now - BackupRules.RunningLost;
        if (await db.BackupRuns.AnyAsync(r => r.JobId == jobId && r.Status == BackupRules.Running && r.StartedAt > lost, ct))
            return CommandResult.Conflict("Ese respaldo ya se está haciendo.");

        var uid = Guid.NewGuid();
        var expires = CommandSigning.Timestamp(Now + BackupRules.RunNowLifetime);
        var signature = await signer.SignAsync(BackupRules.RunNowPayload(machineId, jobId, uid.ToString("D"), full, expires), ct);
        var run = new BackupRun
        {
            JobId = jobId,
            MachineId = machineId,
            RunUid = uid,
            Trigger = "manual",
            RequestedById = user.UserId,
            Status = BackupRules.Running,
            StartedAt = Now,
            Detail = "Pedido desde el panel; esperando al agente.",
        };
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync(full ? "respaldo.completo_ahora" : "respaldo.ahora", "respaldo_trabajos", jobId.ToString(), null,
            new { maquinaId = machineId, ejecucion = uid }, ct);

        var sent = await link.RunNowAsync(user.CompanyId, agentId, new
        {
            maquina_id = machineId,
            trabajo_id = jobId,
            ejecucion = uid.ToString("D"),
            completo = full,
            vence = expires,
            firma = signature,
        }, ct);
        if (!sent)
        {
            run.Status = BackupRules.Failed;
            run.FinishedAt = Now;
            run.Detail = "El agente no recibió el pedido (se desconectó).";
            await db.SaveChangesAsync(ct);
            return CommandResult.Conflict($"El agente de {machine.Name} no está conectado.");
        }
        return CommandResult.Ok(run.Id);
    }

    public async Task<(BackupExploreResult? Result, string? Problem)> ExploreAsync(int machineId, CancellationToken ct)
    {
        var machine = await MachineAsync(machineId, ct);
        if (machine is null)
            return (null, null);
        if (machine.AgentId is not int agentId || !link.IsConnected(user.CompanyId, agentId))
            return (null, $"El agente de {machine.Name} no está conectado.");
        if (!BackupRules.SupportsBackups(machine.AgentVersion))
            return (null, $"Los respaldos necesitan el agente {BackupRules.AgentVersion} o superior (tiene la {machine.AgentVersion ?? "?"}).");
        var data = await link.ExploreAsync(user.CompanyId, agentId, ct);
        if (data is null)
            return (new BackupExploreResult(false, "El agente no respondió a tiempo.", [], [], []), null);
        return (new BackupExploreResult(
            data.Ok, Cut(data.Error, 300),
            (data.Instancias ?? []).Take(20).Select(i => new SqlInstanceInfo(Cut(i.Nombre, 128)!, Cut(i.Error, 300),
                (i.Bases ?? []).Take(500).Select(b => new SqlDatabaseInfo(Cut(b.Nombre, 128)!, b.TamanoMb, Cut(b.Recuperacion, 20))).ToList())).ToList(),
            (data.Servicios ?? []).Take(1000).Select(s => new ServiceInfo(Cut(s.Nombre, 256)!, Cut(s.Mostrar, 256) ?? s.Nombre, Cut(s.Estado, 30) ?? "")).ToList(),
            (data.Discos ?? []).Take(20).Select(d => new ExternalDiskInfo(Cut(d.Serie, 100)!, Cut(d.Nombre, 100) ?? d.Serie, Cut(d.Letra, 3), d.TotalGb, d.LibreGb)).ToList()),
            null);
    }

    public async Task<(BackupDestinationResult? Result, string? Problem)> TestDestinationAsync(int machineId, BackupDestinationInput input, CancellationToken ct)
    {
        var machine = await MachineAsync(machineId, ct);
        if (machine is null)
            return (null, null);
        if (machine.AgentId is not int agentId || machine.TokenHash is null || !link.IsConnected(user.CompanyId, agentId))
            return (null, $"El agente de {machine.Name} no está conectado.");
        if (!BackupRules.SupportsBackups(machine.AgentVersion))
            return (null, $"Los respaldos necesitan el agente {BackupRules.AgentVersion} o superior.");

        string? password = input.RedClave;
        if (string.IsNullOrEmpty(password) && input.TrabajoId is int jobId)
        {
            var stored = await db.BackupJobs.AsNoTracking().Where(j => j.Id == jobId && j.MachineId == machineId)
                .Select(j => j.NetworkEncryptedPassword).FirstOrDefaultAsync(ct);
            password = stored is null ? null : protector.Unprotect(Encoding.UTF8.GetString(stored));
        }
        var destination = Destination(input.DestinoTipo,
            input.DestinoTipo == BackupRules.ExternalDisk ? (string.IsNullOrWhiteSpace(input.DestinoRuta) ? "Centinela" : input.DestinoRuta.Trim()) : input.DestinoRuta?.Trim().TrimEnd('\\') ?? "",
            input.Discos ?? [], false, input.RedUsuario?.Trim(), password, machine.TokenHash);
        var data = await link.TestDestinationAsync(user.CompanyId, agentId, destination, ct);
        return data is null
            ? (new BackupDestinationResult(false, "El agente no respondió a tiempo.", null, null), null)
            : (new BackupDestinationResult(data.Ok, Cut(data.Error, 300), data.LibreGb, Cut(data.Ubicacion, 300)), null);
    }

    /// <summary>El destino como lo recibe el agente: la cuenta de red va sellada para él.</summary>
    internal static object Destination(string type, string path, IReadOnlyList<BackupDisk> disks, bool eject, string? user, string? password, string tokenHash) => new
    {
        tipo = type,
        ruta = path,
        discos = disks.Select(d => new { serie = d.Serie, nombre = d.Nombre }).ToList(),
        expulsar = eject,
        cuenta = type == BackupRules.Network && !string.IsNullOrEmpty(user)
            ? SnmpCredentialSeal.Seal(tokenHash, JsonSerializer.Serialize(new { usuario = user, clave = password ?? "" }))
            : null,
    };

    internal static BackupJobItem ToItem(BackupJob j) => new(
        j.Id, j.Name, j.Source, j.SqlInstance, Strings(j.Items), j.Mode, Strings(j.StopServices), j.ColdMaxMinutes, j.Strategy,
        j.FullEveryDays, j.DestinationType, j.DestinationPath, Disks(j.Disks), j.EjectAfter, j.NetworkUser,
        j.NetworkEncryptedPassword is not null, j.Frequency, BackupRules.ParseWeekDays(j.WeekDays), j.Time, j.KeepDaily,
        j.KeepMonthly, j.Verify, j.IsActive, j.LastRunAt, j.LastStatus, j.LastSuccessAt, j.LastError, j.NextRunAt,
        j.DiskMissingSince, j.ServicesNotRestarted, j.CreatedAt);

    private static BackupRunItem ToItem(BackupRun r, string jobName, string? requestedBy, DateTime now)
    {
        // Sin noticias del agente por horas: se cayó a mitad (o nunca recibió el pedido).
        var lost = r.Status == BackupRules.Running && r.StartedAt < now - BackupRules.RunningLost;
        return new BackupRunItem(r.Id, r.JobId, jobName, r.Trigger, requestedBy, lost ? BackupRules.Failed : r.Status, r.Kind,
            r.StartedAt, r.FinishedAt, r.BytesCopied, r.BytesTotal, r.Files, r.Destination, r.SetName,
            lost ? "El agente dejó de informar a mitad del respaldo." : r.Detail);
    }

    internal static IReadOnlyList<string> Strings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<BackupDisk> Disks(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<BackupDisk>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Lo que va a la auditoría: todo menos la clave.</summary>
    private static object AuditView(BackupJob j) => new
    {
        j.Name, j.Source, j.SqlInstance, Items = Strings(j.Items), j.Mode, StopServices = Strings(j.StopServices), j.ColdMaxMinutes,
        j.Strategy, j.FullEveryDays, j.DestinationType, j.DestinationPath, Disks = Disks(j.Disks), j.EjectAfter, j.NetworkUser,
        j.Frequency, j.WeekDays, j.Time, j.KeepDaily, j.KeepMonthly, j.Verify, j.IsActive,
    };

    internal static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>
/// Lado del gateway: la configuración firmada que recibe el agente del servidor y lo que él informa (ejecuciones y
/// agenda). Cada operación abre su ámbito con la empresa del agente (RLS).
/// </summary>
public sealed class BackupStatusStore(IServiceScopeFactory scopes, AlertEngine alerts, ILogger<BackupStatusStore> logger) : IBackupStatusStore
{
    private static MonitoreoDbContext Open(AsyncServiceScope scope, int companyId)
    {
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        return scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
    }

    public async Task<object?> SignedConfigAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var agent = await db.Agents.AsNoTracking().Where(a => a.Id == identity.AgentId)
            .Select(a => new { a.TokenHash, a.Version }).FirstOrDefaultAsync(cancellationToken);
        if (agent is null || !BackupRules.SupportsBackups(agent.Version))
            return null;
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var jobs = await db.BackupJobs.AsNoTracking().Where(j => j.MachineId == identity.MachineId).OrderBy(j => j.Id).ToListAsync(cancellationToken);

        var list = new List<object>();
        foreach (var j in jobs)
        {
            string? password = null;
            if (j.NetworkEncryptedPassword is not null)
            {
                password = protector.Unprotect(Encoding.UTF8.GetString(j.NetworkEncryptedPassword));
                if (password is null)
                    logger.LogWarning("Respaldo {JobId}: no se pudo abrir la clave de la carpeta de red.", j.Id);
            }
            list.Add(new
            {
                id = j.Id,
                nombre = j.Name,
                origen = j.Source,
                instancia_sql = j.SqlInstance,
                elementos = Backups.Strings(j.Items),
                modo = j.Mode,
                servicios_detener = Backups.Strings(j.StopServices),
                frio_max_minutos = j.ColdMaxMinutes,
                estrategia = j.Strategy,
                completo_cada_dias = j.FullEveryDays,
                destino = Backups.Destination(j.DestinationType, j.DestinationPath, Backups.Disks(j.Disks), j.EjectAfter, j.NetworkUser, password, agent.TokenHash),
                frecuencia = j.Frequency,
                dias_semana = BackupRules.ParseWeekDays(j.WeekDays),
                hora = j.Time,
                retener_diarios = j.KeepDaily,
                retener_mensuales = j.KeepMonthly,
                verificar = j.Verify,
                activo = j.IsActive,
            });
        }
        var config = JsonSerializer.Serialize(new { maquina_id = identity.MachineId, trabajos = list });
        var signature = await scope.ServiceProvider.GetRequiredService<ICompanySigner>()
            .SignAsync(BackupRules.ConfigPayload(identity.MachineId, config), cancellationToken);
        return new { config, firma = signature };
    }

    public async Task SaveRunAsync(AgentIdentity identity, RespaldoEstadoData data, DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        if (!BackupRules.Statuses.Contains(data.Estado) || !Guid.TryParse(data.Ejecucion, out var uid))
            return;
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var job = await db.BackupJobs.FirstOrDefaultAsync(j => j.Id == data.TrabajoId && j.MachineId == identity.MachineId, cancellationToken);
        if (job is null)
        {
            logger.LogWarning("Agente {AgentId}: estado de un respaldo ({JobId}) que no es de su equipo; se ignora.", identity.AgentId, data.TrabajoId);
            return;
        }

        var run = await db.BackupRuns.FirstOrDefaultAsync(r => r.RunUid == uid && r.JobId == job.Id, cancellationToken);
        if (run is null)
        {
            run = new BackupRun { JobId = job.Id, MachineId = identity.MachineId, RunUid = uid, Trigger = data.Disparo == "manual" ? "manual" : "programado" };
            db.BackupRuns.Add(run);
        }
        else if (run.Status != BackupRules.Running)
            return; // Ya terminó: un aviso repetido o atrasado no lo cambia.

        var started = data.Inicio.Kind == DateTimeKind.Utc ? data.Inicio : data.Inicio.ToUniversalTime();
        run.StartedAt = started > receivedAtUtc.AddMinutes(5) || started < receivedAtUtc.AddDays(-3) ? receivedAtUtc : started;
        run.Status = data.Estado;
        run.Kind = data.Tipo is not null && BackupRules.Kinds.Contains(data.Tipo) ? data.Tipo : run.Kind;
        run.BytesCopied = Math.Max(0, data.BytesCopiados);
        run.BytesTotal = Math.Max(0, data.BytesTotales);
        run.Files = data.Archivos;
        run.Destination = Backups.Cut(data.Destino, 300) ?? run.Destination;
        run.SetName = Backups.Cut(data.Conjunto, 200) ?? run.SetName;
        run.Detail = Backups.Cut(data.Detalle, 1000);

        var final = data.Estado != BackupRules.Running;
        if (final)
        {
            run.FinishedAt = receivedAtUtc;
            job.LastRunAt = receivedAtUtc;
            job.LastStatus = data.Estado;
            job.LastError = data.Estado == BackupRules.Ok ? null : Backups.Cut(data.Detalle, 400);
            if (data.Estado is BackupRules.Ok or BackupRules.Warning)
            {
                job.LastSuccessAt = receivedAtUtc;
                job.DiskMissingSince = null;
            }
            var stuck = (data.ServiciosSinArrancar ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Take(20).ToList();
            job.ServicesNotRestarted = stuck.Count == 0 ? null : Backups.Cut(string.Join(", ", stuck), 400);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (final)
            await alerts.EvaluateCompanyAsync(identity.CompanyId, cancellationToken, [identity.MachineId]);
    }

    public async Task SaveScheduleAsync(AgentIdentity identity, RespaldosAgendaData data, DateTime receivedAtUtc, CancellationToken cancellationToken)
    {
        if (data.Trabajos is not { Count: > 0 } items)
            return;
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var ids = items.Select(i => i.TrabajoId).ToList();
        var jobs = await db.BackupJobs.Where(j => j.MachineId == identity.MachineId && ids.Contains(j.Id)).ToListAsync(cancellationToken);
        var diskChanged = false;
        foreach (var job in jobs)
        {
            var item = items.First(i => i.TrabajoId == job.Id);
            job.NextRunAt = item.Proxima is { } next ? (next.Kind == DateTimeKind.Utc ? next : next.ToUniversalTime()) : null;
            if (item.DiscoConectado == false && job.DiskMissingSince is null && job.IsActive)
            {
                job.DiskMissingSince = receivedAtUtc;
                diskChanged = true;
            }
            else if (item.DiscoConectado == true && job.DiskMissingSince is not null)
            {
                job.DiskMissingSince = null;
                diskChanged = true;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (diskChanged)
            await alerts.EvaluateCompanyAsync(identity.CompanyId, cancellationToken, [identity.MachineId]);
    }
}

using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>
/// Biblioteca de scripts (W-33) y ejecución en equipos (W-32). Todo se firma con la llave de la empresa: el agente
/// rechaza lo que no venga firmado (sección 5). El contenido de un comando nunca se escribe en los logs.
/// </summary>
public sealed class RemoteCommands(
    MonitoreoDbContext db, ICurrentUser user, IAuditTrail audit, ICompanySigner signer, ICommandDispatcher dispatcher, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ biblioteca

    public async Task<IReadOnlyList<ScriptItem>> ScriptsAsync(CancellationToken ct)
    {
        var rows = await db.Scripts.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Language,
                s.Description,
                s.Content,
                s.CreatedAt,
                s.UpdatedAt,
                s.Signature,
                CreatedBy = db.Users.Where(u => u.Id == s.CreatedById).Select(u => u.Name).FirstOrDefault(),
                LastRun = db.Commands.Where(c => c.ScriptId == s.Id).Max(c => (DateTime?)c.CreatedAt),
            })
            .ToListAsync(ct);
        return rows.Select(s => new ScriptItem(
            s.Id, s.Name, Text(s.Language), s.Description, s.Content, s.CreatedBy ?? "", s.CreatedAt, s.UpdatedAt, s.Signature is not null, s.LastRun)).ToList();
    }

    public async Task<CommandResult> SaveScriptAsync(int? id, ScriptInput input, CancellationToken ct)
    {
        Script? row = null;
        if (id is int existing && (row = await db.Scripts.FirstOrDefaultAsync(s => s.Id == existing, ct)) is null)
            return CommandResult.NotFound();

        var name = input.Nombre.Trim();
        if (await db.Scripts.AnyAsync(s => s.Name == name && s.Id != (id ?? 0), ct))
            return CommandResult.Conflict("Ya existe un script con ese nombre.");

        var before = row is null ? null : Snapshot(row);
        if (row is null)
        {
            row = db.Scripts.Add(new Script { CreatedById = user.UserId }).Entity;
        }
        else
        {
            row.UpdatedAt = Now;
        }

        row.Name = name;
        row.Language = ParseLanguage(input.Lenguaje);
        row.Description = string.IsNullOrWhiteSpace(input.Descripcion) ? null : input.Descripcion.Trim();
        row.Content = input.Contenido.Replace("\r\n", "\n");
        row.Signature = await signer.SignAsync(CommandSigning.ScriptPayload(user.CompanyId, input.Lenguaje, row.Content), ct);
        await db.SaveChangesAsync(ct);

        // En la auditoría va el hash del contenido, no el contenido (puede llevar rutas o datos internos).
        await audit.RecordAsync(before is null ? "script.crear" : "script.editar", "scripts", row.Id.ToString(), before, Snapshot(row), ct);
        return CommandResult.Ok(row.Id);
    }

    public async Task<CommandResult> DeleteScriptAsync(int id, CancellationToken ct)
    {
        var row = await db.Scripts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null)
            return CommandResult.NotFound();
        var task = await db.ScheduledTasks.Where(t => t.ScriptId == id).Select(t => t.Name).FirstOrDefaultAsync(ct);
        if (task is not null)
            return CommandResult.Conflict($"La tarea programada \"{task}\" usa este script: elimínela o cámbiele el script primero.");
        db.Scripts.Remove(row);
        await db.SaveChangesAsync(ct);
        await audit.RecordAsync("script.eliminar", "scripts", id.ToString(), Snapshot(row), null, ct);
        return CommandResult.Ok(id);
    }

    // ------------------------------------------------------------------ ejecutar

    public async Task<(RunResult? Result, CommandResult? Error)> RunAsync(RunInput input, CancellationToken ct)
    {
        string language, content;
        Script? script = null;
        if (input.ScriptId is int scriptId)
        {
            script = await db.Scripts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scriptId, ct);
            if (script is null)
                return (null, CommandResult.Invalid("El script no existe."));
            language = Text(script.Language);
            content = script.Content;
            if (!await IsSignedAsync(signer, user.CompanyId, script, ct))
                return (null, CommandResult.Conflict(InvalidSignatureMessage));
        }
        else
        {
            language = input.Lenguaje!;
            content = input.Contenido!.Replace("\r\n", "\n");
        }

        var allowed = await user.AllowedBranchIdsAsync(ct);
        var ids = input.MaquinaIds.Distinct().ToList();
        var machines = await db.Machines.AsNoTracking()
            .Where(m => ids.Contains(m.Id) && m.Status != MachineStatus.Retired && (allowed == null || allowed.Contains(m.BranchId)))
            .Where(m => db.Agents.Any(a => a.MachineId == m.Id && !a.IsRevoked && a.IsApproved))
            .Select(m => m.Id)
            .ToListAsync(ct);
        if (machines.Count != ids.Count)
            return (null, CommandResult.Invalid("Algún equipo no existe, no tiene agente o no tiene acceso a él."));

        var timeout = input.TimeoutS ?? RemoteRules.DefaultTimeoutSeconds;
        var (batch, rows) = await CreateBatchAsync(db, script, language, content, machines, user.UserId, timeout, Now, ct);
        await audit.RecordAsync("comando.ejecutar", "comandos", batch.ToString(), null, AuditDetail(batch, script, language, content, machines, timeout), ct);

        await dispatcher.DispatchAsync(user.CompanyId, rows.Select(r => r.Id).ToList(), ct);
        return (new RunResult(batch, rows.Count), null);
    }

    public const string InvalidSignatureMessage = "La firma del script no es válida: ábralo y guárdelo de nuevo desde el panel.";

    /// <summary>Un script cambiado directamente en la base (sin pasar por el panel) pierde su firma y no se ejecuta.</summary>
    public static async Task<bool> IsSignedAsync(ICompanySigner signer, int companyId, Script script, CancellationToken ct) =>
        script.Signature is not null
        && await signer.VerifyAsync(CommandSigning.ScriptPayload(companyId, Text(script.Language), script.Content), script.Signature, ct);

    /// <summary>Un lote: un comando por equipo, todos con el mismo vencimiento (sin fracciones de segundo: viaja firmado).</summary>
    public static async Task<(Guid Batch, List<Command> Rows)> CreateBatchAsync(
        MonitoreoDbContext db, Script? script, string language, string content, IReadOnlyCollection<int> machines, int userId, int timeout,
        DateTime now, CancellationToken ct)
    {
        var batch = Guid.NewGuid();
        var expires = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc) + RemoteRules.Expiry;
        var rows = machines.Select(machineId => new Command
        {
            MachineId = machineId,
            UserId = userId,
            ScriptId = script?.Id,
            Type = script is null ? CommandType.Command : CommandType.Script,
            BatchId = batch,
            Language = ParseLanguage(language),
            Content = content,
            TimeoutSeconds = timeout,
            ExpiresAt = expires,
        }).ToList();
        db.Commands.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return (batch, rows);
    }

    /// <summary>En la auditoría va el hash del contenido, no el contenido.</summary>
    public static object AuditDetail(Guid batch, Script? script, string language, string content, IReadOnlyCollection<int> machines, int timeout) => new
    {
        lote = batch,
        scriptId = script?.Id,
        script = script?.Name,
        lenguaje = language,
        sha256 = CommandSigning.Sha256Hex(content),
        maquinas = machines,
        timeoutS = timeout,
    };

    // ------------------------------------------------------------------ resultados

    private async Task<IQueryable<Command>> VisibleAsync(CancellationToken ct)
    {
        var allowed = await user.AllowedBranchIdsAsync(ct);
        var machines = db.Machines.Where(m => allowed == null || allowed.Contains(m.BranchId)).Select(m => m.Id);
        return db.Commands.AsNoTracking().Where(c => machines.Contains(c.MachineId));
    }

    /// <summary>Las ejecuciones más recientes (por lote), opcionalmente de un equipo.</summary>
    public async Task<IReadOnlyList<RunSummary>> RunsAsync(int? machineId, CancellationToken ct)
    {
        var visible = await VisibleAsync(ct);
        if (machineId is int machine)
            visible = visible.Where(c => c.MachineId == machine);

        var batches = await visible.Where(c => c.BatchId != null)
            .GroupBy(c => c.BatchId)
            .Select(g => new { BatchId = g.Key!.Value, CreatedAt = g.Max(c => c.CreatedAt) })
            .OrderByDescending(g => g.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        var ids = batches.Select(b => (Guid?)b.BatchId).ToList();
        var rows = await visible.Where(c => ids.Contains(c.BatchId))
            .Select(c => new CommandRow(c.BatchId!.Value, c.ScriptId, c.Language, c.Content, c.UserId, c.CreatedAt, c.Status))
            .ToListAsync(ct);
        return await SummariesAsync(rows, ct);
    }

    public async Task<RunDetail?> RunAsync(Guid batchId, CancellationToken ct)
    {
        var visible = (await VisibleAsync(ct)).Where(c => c.BatchId == batchId);
        var commands = await visible
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id,
                c.BatchId,
                c.MachineId,
                c.ScriptId,
                c.Language,
                c.Content,
                c.UserId,
                c.CreatedAt,
                c.Status,
                c.ExitCode,
                c.SentAt,
                c.CompletedAt,
                c.Result,
                c.TimeoutSeconds,
                Machine = db.Machines.Where(m => m.Id == c.MachineId).Select(m => new { m.Name, Branch = m.Branch!.Name }).First(),
            })
            .ToListAsync(ct);
        if (commands.Count == 0)
            return null;

        var summary = (await SummariesAsync(
            commands.Select(c => new CommandRow(batchId, c.ScriptId, c.Language, c.Content, c.UserId, c.CreatedAt, c.Status)).ToList(), ct)).Single();
        var items = commands.Select(c => new CommandItem(
            c.Id, c.BatchId, c.MachineId, c.Machine.Name, c.Machine.Branch, Text(c.Status), c.ExitCode, c.CreatedAt, c.SentAt, c.CompletedAt, c.Result)).ToList();
        return new RunDetail(summary, commands[0].Content ?? "", commands[0].TimeoutSeconds, items);
    }

    private sealed record CommandRow(Guid BatchId, int? ScriptId, ScriptLanguage? Language, string? Content, int UserId, DateTime CreatedAt, CommandStatus Status);

    private async Task<IReadOnlyList<RunSummary>> SummariesAsync(IReadOnlyList<CommandRow> rows, CancellationToken ct)
    {
        var scriptIds = rows.Where(r => r.ScriptId is not null).Select(r => r.ScriptId!.Value).Distinct().ToList();
        var scripts = await db.Scripts.AsNoTracking().Where(s => scriptIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var userIds = rows.Select(r => r.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        return rows.GroupBy(r => r.BatchId)
            .Select(g =>
            {
                var first = g.First();
                int Count(params CommandStatus[] statuses) => g.Count(r => statuses.Contains(r.Status));
                return new RunSummary(
                    g.Key,
                    RemoteRules.Title(first.ScriptId is int s ? scripts.GetValueOrDefault(s) : null, first.Content ?? ""),
                    first.Language is { } language ? Text(language) : "",
                    users.GetValueOrDefault(first.UserId) ?? "",
                    g.Min(r => r.CreatedAt),
                    g.Count(),
                    Count(CommandStatus.Pending, CommandStatus.Sent),
                    Count(CommandStatus.Running),
                    Count(CommandStatus.Completed),
                    Count(CommandStatus.Failed, CommandStatus.Expired));
            })
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    internal static ScriptLanguage ParseLanguage(string value) => value switch
    {
        "cmd" => ScriptLanguage.Cmd,
        "bash" => ScriptLanguage.Bash,
        "python" => ScriptLanguage.Python,
        _ => ScriptLanguage.PowerShell,
    };

    private static object Snapshot(Script s) => new
    {
        nombre = s.Name,
        lenguaje = Text(s.Language),
        descripcion = s.Description,
        sha256 = CommandSigning.Sha256Hex(s.Content),
    };
}

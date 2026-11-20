using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Monitoring;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using MonitoreoTI.Infrastructure.Remote;

namespace MonitoreoTI.Infrastructure.Panel;

/// <summary>
/// Errores del equipo resueltos desde Centinela (decisión 91): agrupados por origen y código, marcados resueltos o
/// ignorados (en el equipo o en la empresa) y reparados con un comando firmado cuando el catálogo sabe cómo.
/// </summary>
public sealed class ErrorGroups(
    MonitoreoDbContext db,
    PanelQueries panel,
    ICurrentUser user,
    IAuditTrail audit,
    TenantContext tenant,
    TimeProvider clock,
    ICommandDispatcher dispatcher)
{
    /// <summary>Lo que se le da al reinicio: SQL Server con mucha memoria tarda en detenerse.</summary>
    public const int FixTimeoutSeconds = 180;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    internal sealed record GroupRow(string? Source, int? Code, int Count, DateTime FirstAt, DateTime LastAt, bool Critical, long LastId);

    internal sealed record Loaded(GroupRow Row, ErrorGroupMark? Mark, int OpenCount, ErrorGroupState? State);

    /// <summary>Grupos del rango con su marca y cuántas ocurrencias siguen abiertas (lo usan la lista y la ficha).</summary>
    internal static async Task<List<Loaded>> LoadAsync(MonitoreoDbContext db, int machineId, DateTime since, CancellationToken ct)
    {
        var rows = await db.ErrorEvents.AsNoTracking()
            .Where(e => e.MachineId == machineId && e.Timestamp >= since && (e.Level == EventLevel.Error || e.Level == EventLevel.Critical))
            .GroupBy(e => new { e.Source, e.Code })
            .Select(g => new
            {
                g.Key.Source,
                g.Key.Code,
                Count = g.Count(),
                FirstAt = g.Min(e => e.Timestamp),
                LastAt = g.Max(e => e.Timestamp),
                Critical = g.Max(e => e.Level == EventLevel.Critical ? 1 : 0),
                LastId = g.Max(e => e.Id),
            })
            .ToListAsync(ct);
        if (rows.Count == 0)
            return [];

        var states = await db.ErrorGroupStates.AsNoTracking()
            .Where(s => s.MachineId == machineId || s.MachineId == null)
            .ToListAsync(ct);
        var marks = states.Select(ToMark).ToList();

        var result = new List<Loaded>(rows.Count);
        foreach (var r in rows)
        {
            var row = new GroupRow(r.Source, r.Code, r.Count, r.FirstAt, r.LastAt, r.Critical == 1, r.LastId);
            var mark = ErrorGroupRules.MarkFor(marks, machineId, r.Source, r.Code);
            var open = mark is null ? r.Count
                : mark.Ignored || r.LastAt <= mark.At ? 0
                : await db.ErrorEvents.CountAsync(e => e.MachineId == machineId && e.Source == r.Source && e.Code == r.Code
                    && e.Timestamp > mark.At && e.Timestamp >= since
                    && (e.Level == EventLevel.Error || e.Level == EventLevel.Critical), ct);
            var state = mark is null ? null
                : states.FirstOrDefault(s => s.MachineId == mark.MachineId && s.IsIgnored == mark.Ignored && ErrorGroupRules.SameGroup(s.Source, s.Code, r.Source, r.Code));
            result.Add(new Loaded(row, mark, open, state));
        }
        return result;
    }

    /// <summary>Errores abiertos del equipo en los últimos días (la insignia de la pestaña Errores).</summary>
    internal static async Task<int> OpenCountAsync(MonitoreoDbContext db, int machineId, DateTime since, CancellationToken ct) =>
        (await LoadAsync(db, machineId, since, ct)).Sum(g => g.OpenCount);

    internal static ErrorGroupMark ToMark(ErrorGroupState s) => new(s.MachineId, s.Source, s.Code, s.IsIgnored, s.At);

    // ------------------------------------------------------------------ lecturas

    public async Task<ErrorGroupList?> ListAsync(int machineId, int days, CancellationToken ct)
    {
        if (!await panel.CanSeeMachineAsync(machineId, ct))
            return null;
        days = Math.Clamp(days, 1, ErrorGroupRules.MaxDays);
        var loaded = await LoadAsync(db, machineId, Now.AddDays(-days), ct);

        var ids = loaded.Select(g => g.Row.LastId).ToList();
        var messages = ids.Count == 0
            ? new Dictionary<long, string?>()
            : await db.ErrorEvents.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Message, ct);
        var services = await db.Services.AsNoTracking().Where(s => s.MachineId == machineId)
            .Select(s => new KnownService(s.Name, s.DisplayName)).ToListAsync(ct);
        var userIds = loaded.Select(g => g.State?.UserId).OfType<int>().Distinct().ToList();
        var names = userIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name, ct);

        var groups = loaded.Select(g =>
            {
                var explanation = EventCatalog.Explain(g.Row.Source, g.Row.Code);
                var message = messages.GetValueOrDefault(g.Row.LastId);
                var state = ErrorGroupRules.StateOf(g.Mark, g.Row.LastAt);
                return new ErrorGroupItem(
                    g.Row.Source,
                    g.Row.Code,
                    g.Row.Critical ? "critico" : "error",
                    g.Row.Count,
                    g.Row.FirstAt,
                    g.Row.LastAt,
                    explanation?.Title,
                    explanation?.Detail,
                    explanation?.Advice,
                    message,
                    state,
                    g.Mark is null ? null : g.Mark.MachineId is null ? "empresa" : "equipo",
                    g.State?.UserId is int uid ? names.GetValueOrDefault(uid) : null,
                    g.State?.At,
                    g.State?.Note,
                    g.OpenCount,
                    ErrorGroupRules.ServiceFor(explanation, g.Row.Source, message, services),
                    explanation?.Noise ?? false);
            })
            // Lo que sigue abierto primero (lo crítico y lo más repetido arriba); resueltos e ignorados al final.
            .OrderBy(g => g.State is ErrorGroupRules.Open or ErrorGroupRules.Reappeared ? 0 : g.State == ErrorGroupRules.Resolved ? 1 : 2)
            .ThenByDescending(g => g.Level == "critico")
            .ThenByDescending(g => g.OpenCount)
            .ThenByDescending(g => g.LastAt)
            .ToList();
        return new ErrorGroupList(days, groups.Sum(g => g.OpenCount), groups);
    }

    /// <summary>Las ocurrencias de un grupo, la más reciente primero.</summary>
    public async Task<IReadOnlyList<ErrorEventItem>?> OccurrencesAsync(int machineId, string? source, int? code, CancellationToken ct)
    {
        if (!await panel.CanSeeMachineAsync(machineId, ct))
            return null;
        var rows = await db.ErrorEvents.AsNoTracking()
            .Where(e => e.MachineId == machineId && e.Source == source && e.Code == code)
            .OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).Take(100).ToListAsync(ct);
        return rows.Select(e => new ErrorEventItem(e.Id, e.Timestamp, PanelQueries.Text(e.Level), e.Source, e.Code, e.Message, e.SimpleMessage)).ToList();
    }

    // ------------------------------------------------------------------ escrituras

    /// <summary>Marca el grupo resuelto (hasta ahora) o ignorado, en el equipo o en toda la empresa.</summary>
    public async Task<CommandResult> MarkAsync(int machineId, ErrorGroupMarkInput input, CancellationToken ct)
    {
        if (!await panel.CanSeeMachineAsync(machineId, ct))
            return CommandResult.NotFound();
        var company = input.Alcance == "empresa";
        if (company && await user.AllowedBranchIdsAsync(ct) is not null)
            return CommandResult.Invalid("Solo quien ve todas las sucursales puede ignorar un error en toda la empresa.");
        var source = Clean(input.Origen);
        if (!await db.ErrorEvents.AnyAsync(e => e.MachineId == machineId && e.Source == source && e.Code == input.Codigo, ct))
            return CommandResult.Invalid("El equipo no tiene ese error.");

        int? target = company ? null : machineId;
        var state = await db.ErrorGroupStates.FirstOrDefaultAsync(s => s.MachineId == target && s.Source == source && s.Code == input.Codigo, ct);
        var before = state is null ? null : Snapshot(state);
        if (state is null)
        {
            state = new ErrorGroupState { MachineId = target, Source = source, Code = input.Codigo };
            db.ErrorGroupStates.Add(state);
        }
        state.IsIgnored = input.Estado == ErrorGroupRules.Ignored;
        state.Note = Clean(input.Nota);
        state.UserId = user.UserId;
        state.At = Now;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(state.IsIgnored ? "errores.ignorar" : "errores.resolver", "errores_estado", state.Id.ToString(),
            before, Snapshot(state), ct);
        return CommandResult.Ok(state.Id);
    }

    /// <summary>Quita la marca del equipo (o la de la empresa): el grupo vuelve a contar.</summary>
    public async Task<CommandResult> ReopenAsync(int machineId, ErrorGroupKeyInput input, CancellationToken ct)
    {
        if (!await panel.CanSeeMachineAsync(machineId, ct))
            return CommandResult.NotFound();
        var company = input.Alcance == "empresa";
        if (company && await user.AllowedBranchIdsAsync(ct) is not null)
            return CommandResult.Invalid("Solo quien ve todas las sucursales puede cambiar un error ignorado en toda la empresa.");

        var source = Clean(input.Origen);
        int? target = company ? null : machineId;
        var state = await db.ErrorGroupStates.FirstOrDefaultAsync(s => s.MachineId == target && s.Source == source && s.Code == input.Codigo, ct);
        if (state is null)
            return CommandResult.NotFound();
        var before = Snapshot(state);
        db.ErrorGroupStates.Remove(state);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("errores.reabrir", "errores_estado", state.Id.ToString(), before, null, ct);
        return CommandResult.Ok(state.Id);
    }

    /// <summary>
    /// Reparar el error con la acción del catálogo (hoy: reiniciar el servicio). El servicio lo decide el servidor con el
    /// último mensaje del grupo y los servicios que informó el agente; el panel solo dice qué grupo.
    /// </summary>
    public async Task<(CommandResult Result, Guid? Batch, string? Service)> FixAsync(int machineId, ErrorGroupKeyInput input, CancellationToken ct)
    {
        if (!await panel.CanSeeMachineAsync(machineId, ct))
            return (CommandResult.NotFound(), null, null);
        var source = Clean(input.Origen);
        var last = await db.ErrorEvents.AsNoTracking()
            .Where(e => e.MachineId == machineId && e.Source == source && e.Code == input.Codigo)
            .OrderByDescending(e => e.Timestamp).Select(e => new { e.Source, e.Message }).FirstOrDefaultAsync(ct);
        if (last is null)
            return (CommandResult.Invalid("El equipo no tiene ese error."), null, null);

        var services = await db.Services.AsNoTracking().Where(s => s.MachineId == machineId)
            .Select(s => new KnownService(s.Name, s.DisplayName)).ToListAsync(ct);
        if (ErrorGroupRules.ServiceFor(EventCatalog.Explain(last.Source, input.Codigo), last.Source, last.Message, services) is not { } service)
            return (CommandResult.Invalid("Este error no tiene una reparación automática en este equipo."), null, null);
        if (!await ServiceRepair.HasAgentAsync(db, machineId, ct))
            return (CommandResult.Invalid("El equipo no tiene un agente que pueda recibir el comando."), null, null);

        var script = ServiceRepairRules.RestartScript(service);
        var since = Now.AddHours(-1);
        if (await db.Commands.CountAsync(c => c.MachineId == machineId && c.Content == script && c.CreatedAt >= since, ct) >= ErrorGroupRules.MaxFixesPerHour)
            return (CommandResult.Conflict($"Ya se reinició {service} {ErrorGroupRules.MaxFixesPerHour} veces en la última hora. Revise el equipo antes de volver a intentarlo."), null, null);

        var (batch, rows) = await RemoteCommands.CreateBatchAsync(
            db, null, ServiceRepairRules.Language, script, [machineId], user.UserId, FixTimeoutSeconds, Now, ct);
        await audit.RecordAsync("errores.reparar", "maquinas", machineId.ToString(), null,
            new { origen = source, codigo = input.Codigo, servicio = service, lote = batch }, ct);
        if (tenant.CompanyId is int company)
            await dispatcher.DispatchAsync(company, [rows[0].Id], ct);
        return (CommandResult.Ok(machineId), batch, service);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static object Snapshot(ErrorGroupState s) => new
    {
        maquinaId = s.MachineId,
        origen = s.Source,
        codigo = s.Code,
        estado = s.IsIgnored ? ErrorGroupRules.Ignored : ErrorGroupRules.Resolved,
        nota = s.Note,
    };
}

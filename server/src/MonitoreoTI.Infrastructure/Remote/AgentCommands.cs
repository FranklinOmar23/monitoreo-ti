using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Abstractions;
using MonitoreoTI.Application.Agents;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Entities.Support;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;
using static MonitoreoTI.Infrastructure.Panel.PanelQueries;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>"ejecutar_comando" listo para el agente: firmado para ese equipo y con su vencimiento.</summary>
public sealed record SignedCommand(long ComandoId, int MaquinaId, string Tipo, string Lenguaje, string Contenido, int TimeoutS, string Vence, string Firma);

/// <summary>
/// Lado del gateway de los comandos: firma lo que se entrega a cada agente, guarda lo que responde y vence lo que
/// nunca se entregó. Cada operación abre su ámbito con la empresa del agente (sin saltar RLS).
/// </summary>
public sealed class AgentCommands(IServiceScopeFactory scopes, IRealtimeNotifier notifier, TimeProvider clock, ILogger<AgentCommands> logger)
{
    /// <summary>Tras el tiempo límite, cuánto más se espera el final de un comando antes de darlo por perdido.</summary>
    public static readonly TimeSpan RunningGrace = TimeSpan.FromMinutes(10);

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Llave pública de la empresa para la bienvenida (se crea si la empresa aún no tiene).</summary>
    public async Task<string> PublicKeyAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        Open(scope, companyId);
        return await scope.ServiceProvider.GetRequiredService<ICompanySigner>().PublicKeyAsync(cancellationToken);
    }

    /// <summary>
    /// Comandos por entregar a ese agente: los indicados (o, sin lista, todos los pendientes y enviados sin respuesta),
    /// firmados y marcados como enviados. El agente ignora un id que ya ejecutó, así que reenviar es seguro.
    /// </summary>
    public async Task<IReadOnlyList<SignedCommand>> TakeForAgentAsync(AgentIdentity identity, IReadOnlyCollection<long>? ids, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var signer = scope.ServiceProvider.GetRequiredService<ICompanySigner>();
        var now = Now;

        var query = db.Commands.Where(c => c.MachineId == identity.MachineId
            && (c.Status == CommandStatus.Pending || c.Status == CommandStatus.Sent) && c.ExpiresAt > now);
        if (ids is not null)
            query = query.Where(c => ids.Contains(c.Id));
        var rows = await query.OrderBy(c => c.Id).Take(50).ToListAsync(cancellationToken);

        var result = new List<SignedCommand>();
        foreach (var c in rows)
        {
            var type = Text(c.Type);
            var language = c.Language is { } l ? Text(l) : "powershell";
            var content = c.Content ?? "";
            var expires = CommandSigning.Timestamp(c.ExpiresAt!.Value);
            var signature = await signer.SignAsync(
                CommandSigning.CommandPayload(c.Id, c.MachineId, type, language, c.TimeoutSeconds, expires, content), cancellationToken);
            result.Add(new SignedCommand(c.Id, c.MachineId, type, language, content, c.TimeoutSeconds, expires, signature));
            c.Status = CommandStatus.Sent;
            c.SentAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var c in rows)
            await NotifyAsync(db, c, cancellationToken);
        return result;
    }

    /// <summary>Equipos de esos comandos (para buscar sus agentes conectados).</summary>
    public async Task<IReadOnlyList<int>> MachinesOfAsync(int companyId, IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, companyId);
        return await db.Commands.Where(c => ids.Contains(c.Id)).Select(c => c.MachineId).Distinct().ToListAsync(cancellationToken);
    }

    /// <summary>Si el envío falló (el agente se desconectó justo), el comando vuelve a pendiente para la próxima conexión.</summary>
    public async Task ReturnToPendingAsync(AgentIdentity identity, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var list = ids.ToList();
        await db.Commands.Where(c => list.Contains(c.Id) && c.MachineId == identity.MachineId && c.Status == CommandStatus.Sent)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CommandStatus.Pending), cancellationToken);
    }

    /// <summary>"resultado_comando": avance o final. Solo cuenta para un comando de ese mismo equipo.</summary>
    public async Task SaveResultAsync(AgentIdentity identity, CommandResultData data, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, identity.CompanyId);
        var command = await db.Commands.FirstOrDefaultAsync(c => c.Id == data.ComandoId && c.MachineId == identity.MachineId, cancellationToken);
        if (command is null)
        {
            logger.LogWarning("Agente {AgentId}: informó el comando {CommandId}, que no es de su equipo; se ignora.", identity.AgentId, data.ComandoId);
            return;
        }

        // Un comando terminado no vuelve atrás (un "ejecutando" retrasado en la cola llega después del final).
        if (command.Status is CommandStatus.Completed or CommandStatus.Failed or CommandStatus.Expired)
            return;

        switch (data.Estado)
        {
            case "ejecutando":
                command.Status = CommandStatus.Running;
                command.SentAt ??= Now;
                break;
            case "completado":
            case "error":
                command.Status = data.Estado == "completado" ? CommandStatus.Completed : CommandStatus.Failed;
                command.ExitCode = data.CodigoSalida;
                command.Result = Trim(Join(data.Salida, data.Error));
                command.CompletedAt = Now;
                break;
            case "rechazado":
                command.Status = CommandStatus.Failed;
                command.Result = $"El agente lo rechazó: {Trim(data.Error) ?? "firma no válida"}";
                command.CompletedAt = Now;
                // Un rechazo puede ser un intento de ejecución no autorizada: queda en auditoría como acción del sistema.
                db.AuditLogs.Add(new AuditLog
                {
                    Action = "comando.rechazado",
                    Entity = "comandos",
                    EntityId = command.Id.ToString(),
                    Details = System.Text.Json.JsonSerializer.Serialize(new { maquinaId = command.MachineId, motivo = Trim(data.Error) }),
                });
                logger.LogWarning("Agente {AgentId}: rechazó el comando {CommandId} ({Reason}).", identity.AgentId, command.Id, data.Error);
                break;
            default:
                logger.LogWarning("Agente {AgentId}: estado de comando desconocido {Estado}; se ignora.", identity.AgentId, data.Estado);
                return;
        }

        await db.SaveChangesAsync(cancellationToken);
        await NotifyAsync(db, command, cancellationToken);

        // Un servicio iniciado desde Centinela: su alerta se resuelve ya, sin esperar el próximo reporte del agente.
        if (await ServiceRepair.OnCompletedAsync(db, command, cancellationToken)
            && scope.ServiceProvider.GetService<MonitoreoTI.Infrastructure.Health.AlertEngine>() is { } engine)
            await engine.EvaluateCompanyAsync(identity.CompanyId, cancellationToken, [command.MachineId]);
    }

    /// <summary>
    /// Vence lo que no se entregó a tiempo y da por perdido lo que se quedó "ejecutando" mucho más allá de su
    /// tiempo límite (el equipo se apagó a mitad). Devuelve cuántos cambió.
    /// </summary>
    public async Task<int> ExpireAsync(int companyId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = Open(scope, companyId);
        var now = Now;
        var stale = await db.Commands
            .Where(c => ((c.Status == CommandStatus.Pending || c.Status == CommandStatus.Sent) && c.ExpiresAt <= now)
                || (c.Status == CommandStatus.Running && c.SentAt != null))
            .ToListAsync(cancellationToken);
        stale = stale.Where(c => c.Status != CommandStatus.Running || c.SentAt!.Value.AddSeconds(c.TimeoutSeconds) + RunningGrace <= now).ToList();

        foreach (var c in stale)
        {
            if (c.Status == CommandStatus.Running)
            {
                c.Status = CommandStatus.Failed;
                c.Result = "El equipo no informó el final del comando (¿se apagó o se reinició el agente?).";
            }
            else
            {
                c.Status = CommandStatus.Expired;
                c.Result = "El equipo no se conectó antes del vencimiento.";
            }
            c.CompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var c in stale)
            await NotifyAsync(db, c, cancellationToken);
        return stale.Count;
    }

    private async Task NotifyAsync(MonitoreoDbContext db, Command command, CancellationToken cancellationToken)
    {
        var branchId = await db.Machines.Where(m => m.Id == command.MachineId).Select(m => m.BranchId).FirstOrDefaultAsync(cancellationToken);
        try
        {
            await notifier.CommandChangedAsync(command.CompanyId, branchId, command.Id, command.BatchId, command.MachineId, Text(command.Status), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El aviso en vivo es opcional: el panel también consulta.
            logger.LogDebug(ex, "No se pudo avisar el cambio del comando {CommandId}.", command.Id);
        }
    }

    private static string? Join(string? output, string? error) =>
        string.IsNullOrEmpty(error) ? output : string.IsNullOrEmpty(output) ? error : $"{output}\n{error}";

    private static string? Trim(string? value) =>
        value is null ? null : value.Length > RemoteRules.MaxStoredOutput ? value[..RemoteRules.MaxStoredOutput] + "\n[salida recortada]" : value;

    private static MonitoreoDbContext Open(AsyncServiceScope scope, int companyId)
    {
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetCompany(companyId);
        return scope.ServiceProvider.GetRequiredService<MonitoreoDbContext>();
    }
}

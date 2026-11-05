using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Security;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>
/// Reparar un servicio detenido (decisión 73): un comando firmado de PowerShell que lo inicia, a mano desde la alerta o
/// solo cuando la regla de la sucursal lo pide. El comando viaja como cualquier otro (firma, cola, vencimiento).
/// </summary>
public static class ServiceRepair
{
    /// <summary>Lo que se le da al comando para iniciar el servicio: sobra para cualquier servicio que arranca bien.</summary>
    public const int TimeoutSeconds = 60;

    /// <summary>El equipo tiene un agente aprobado que puede recibir el comando.</summary>
    public static Task<bool> HasAgentAsync(MonitoreoDbContext db, int machineId, CancellationToken ct) =>
        db.Agents.AnyAsync(a => a.MachineId == machineId && !a.IsRevoked && a.IsApproved, ct);

    /// <summary>Cuántas veces se intentó iniciar ese servicio en ese equipo en las últimas 24 h.</summary>
    public static Task<int> AttemptsTodayAsync(MonitoreoDbContext db, int machineId, string service, DateTime now, CancellationToken ct)
    {
        var script = ServiceRepairRules.Script(service);
        var since = now.AddHours(-24);
        return db.Commands.CountAsync(c => c.MachineId == machineId && c.Content == script && c.CreatedAt >= since, ct);
    }

    /// <summary>Crea el comando (sin mandarlo: lo manda quien llama, después de guardar).</summary>
    public static async Task<(Guid Batch, long CommandId)> QueueAsync(
        MonitoreoDbContext db, int machineId, string service, int userId, DateTime now, CancellationToken ct)
    {
        var (batch, rows) = await RemoteCommands.CreateBatchAsync(
            db, null, ServiceRepairRules.Language, ServiceRepairRules.Script(service), [machineId], userId, TimeoutSeconds, now, ct);
        return (batch, rows[0].Id);
    }

    /// <summary>
    /// El agente confirmó que lo inició: el servicio cuenta como en ejecución ya (sin esperar su próximo reporte), así
    /// la alerta se resuelve en la siguiente evaluación. Devuelve true si era un comando de reparación que funcionó.
    /// </summary>
    public static async Task<bool> OnCompletedAsync(MonitoreoDbContext db, Domain.Entities.Support.Command command, CancellationToken ct)
    {
        if (command.Status != CommandStatus.Completed || command.ExitCode is not 0
            || ServiceRepairRules.ServiceFromScript(command.Content) is not { } service)
            return false;
        var row = await db.Services.FirstOrDefaultAsync(s => s.MachineId == command.MachineId && s.Name == service, ct);
        if (row is null)
            return false;
        row.State = "en_ejecucion";
        row.StoppedSince = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Lo que hizo el sistema solo queda en la auditoría como acción sin usuario.</summary>
    public static void AuditAutomatic(MonitoreoDbContext db, int machineId, string service, Guid batch) =>
        db.AuditLogs.Add(new AuditLog
        {
            Action = "servicio.reparar_auto",
            Entity = "maquinas",
            EntityId = machineId.ToString(),
            Details = JsonSerializer.Serialize(new { servicio = service, lote = batch }),
        });
}

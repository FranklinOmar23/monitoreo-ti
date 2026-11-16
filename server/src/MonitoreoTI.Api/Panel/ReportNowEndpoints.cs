using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Api.Agents;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Domain.Enums;
using MonitoreoTI.Infrastructure.Panel;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// POST /api/v1/maquinas/{id}/actualizar (decisión 85): le pide al agente que reporte ya (métricas, volúmenes,
/// procesos y servicios). Lo que llega se ve en la ficha como cualquier reporte (las métricas, en vivo).
/// Pide equipos.ver: no cambia nada en el equipo. Como mucho un pedido cada 10 s por equipo.
/// </summary>
public static class ReportNowEndpoints
{
    private static readonly ConcurrentDictionary<int, DateTime> LastRequest = new();

    public static void MapReportNowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/maquinas/{id:int}/actualizar", async (
                int id, PanelQueries queries, MonitoreoDbContext db, AgentConnectionRegistry registry, TimeProvider clock,
                CancellationToken ct) =>
            {
                if (!await queries.CanSeeMachineAsync(id, ct))
                    return Results.NotFound();
                var agent = await db.Machines.AsNoTracking()
                    .Where(m => m.Id == id && m.Status != MachineStatus.Retired && m.Agent != null && !m.Agent.IsRevoked)
                    .Select(m => new { m.Agent!.Id, m.Agent.Version, m.CompanyId })
                    .FirstOrDefaultAsync(ct);
                if (agent is null)
                    return Problem("El equipo no tiene agente: sus datos no se pueden pedir.", StatusCodes.Status400BadRequest);
                if (!ReportNowRules.Supports(agent.Version))
                    return Problem(
                        $"El agente de este equipo ({agent.Version ?? "sin versión"}) no sabe reportar a pedido. Actualícelo (o reinstálelo) para usar «Actualizar».",
                        StatusCodes.Status409Conflict);
                if (registry.Get(agent.Id) is not { } connection || connection.Identity.CompanyId != agent.CompanyId)
                    return Problem("El agente no está conectado ahora: el equipo está apagado o sin red. Sus datos llegarán cuando vuelva.",
                        StatusCodes.Status409Conflict);

                var now = clock.GetUtcNow().UtcDateTime;
                if (LastRequest.TryGetValue(id, out var last) && now - last < ReportNowRules.Cooldown)
                    return Results.Ok(new { enviado = false, segundos = (int)Math.Ceiling((ReportNowRules.Cooldown - (now - last)).TotalSeconds) });
                LastRequest[id] = now;
                try
                {
                    await connection.SendAsync("reportar_ahora", new { }, ct);
                }
                catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or ObjectDisposedException)
                {
                    LastRequest.TryRemove(id, out _);
                    return Problem("Se perdió la conexión con el agente: inténtelo de nuevo en unos segundos.", StatusCodes.Status409Conflict);
                }
                return Results.Ok(new { enviado = true, segundos = 0 });
            })
            .RequireAuthorization(PermissionPolicies.MachinesView);
    }

    private static IResult Problem(string title, int status) => Results.Problem(title: title, statusCode: status);
}

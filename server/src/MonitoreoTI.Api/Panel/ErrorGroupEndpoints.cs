using FluentValidation;
using MonitoreoTI.Application.Health;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Panel;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Errores del equipo resueltos desde Centinela (decisión 91). Ver: equipos.ver. Marcar resuelto o ignorado:
/// alertas.gestionar. Reparar: es un comando en el equipo (scripts.ejecutar, módulo remoto y 2FA reciente).
/// </summary>
public static class ErrorGroupEndpoints
{
    public static void MapErrorGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var machines = app.MapGroup("/api/v1/maquinas/{id:int}/errores");

        machines.MapGet("/", async (int id, ErrorGroups groups, CancellationToken ct, int? dias) =>
                await groups.ListAsync(id, dias ?? ErrorGroupRules.DefaultDays, ct) is { } list ? Results.Ok(list) : Results.NotFound())
            .RequireAuthorization(PermissionPolicies.MachinesView);
        machines.MapGet("/ocurrencias", async (int id, ErrorGroups groups, CancellationToken ct, string? origen, int? codigo) =>
                await groups.OccurrencesAsync(id, string.IsNullOrWhiteSpace(origen) ? null : origen.Trim(), codigo, ct) is { } list
                    ? Results.Ok(list)
                    : Results.NotFound())
            .RequireAuthorization(PermissionPolicies.MachinesView);

        machines.MapPut("/estado", async (int id, ErrorGroupMarkInput input, IValidator<ErrorGroupMarkInput> validator, ErrorGroups groups, CancellationToken ct) =>
                await ValidateAsync(input, validator, ct) ?? ToResult(await groups.MarkAsync(id, input, ct)))
            .RequireAuthorization(PermissionPolicies.AlertsManage);
        machines.MapPost("/reabrir", async (int id, ErrorGroupKeyInput input, IValidator<ErrorGroupKeyInput> validator, ErrorGroups groups, CancellationToken ct) =>
                await ValidateAsync(input, validator, ct) ?? ToResult(await groups.ReopenAsync(id, input, ct)))
            .RequireAuthorization(PermissionPolicies.AlertsManage);

        machines.MapPost("/reparar", async (int id, ErrorGroupKeyInput input, IValidator<ErrorGroupKeyInput> validator, ErrorGroups groups,
                MonitoreoTI.Infrastructure.Remote.RemoteAccessGuard guard, CancellationToken ct) =>
            {
                if (await ValidateAsync(input, validator, ct) is { } invalid)
                    return invalid;
                // Como cualquier comando: con el módulo remoto y el 2FA confirmado hace poco (decisión 52).
                if (await guard.CheckAsync(ct) is { } denial)
                    return MonitoreoTI.Api.Remote.RemoteEndpoints.Denied(denial);
                var (result, batch, service) = await groups.FixAsync(id, input, ct);
                return result.Succeeded ? Results.Ok(new { lote = batch, servicio = service }) : ToResult(result);
            })
            .RequireAuthorization(PermissionPolicies.ScriptsRun);
    }
}

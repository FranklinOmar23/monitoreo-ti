using FluentValidation;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Panel;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>Alertas (W-20, W-21) y reglas (W-22, W-23). Ver exige alertas.ver; atender y editar reglas, alertas.gestionar.</summary>
public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        var alerts = api.MapGroup("/alertas").RequireAuthorization(PermissionPolicies.AlertsView);
        alerts.MapGet("/", ListAsync);
        alerts.MapGet("/{id:long}", async (long id, AlertQueries q, CancellationToken ct) =>
            await q.AlertAsync(id, ct) is { } alert ? Results.Ok(alert) : Results.NotFound());

        var manage = api.MapGroup("/alertas").RequireAuthorization(PermissionPolicies.AlertsManage);
        manage.MapPost("/{id:long}/atender", async (long id, AlertActionInput input, IValidator<AlertActionInput> validator, AlertCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.AttendAsync(id, input, ct)));
        manage.MapPost("/{id:long}/resolver", async (long id, AlertActionInput input, IValidator<AlertActionInput> validator, AlertCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.ResolveAsync(id, input, ct)));

        // Iniciar el servicio de una alerta de servicio detenido: es ejecutar un comando en el equipo (scripts.ejecutar).
        api.MapPost("/alertas/{id:long}/iniciar-servicio", async (long id, AlertCommands commands, MonitoreoTI.Infrastructure.Remote.RemoteAccessGuard guard, CancellationToken ct) =>
        {
            // Como cualquier comando: con el módulo remoto y el 2FA confirmado hace poco (decisión 52).
            if (await guard.CheckAsync(ct) is { } denial)
                return MonitoreoTI.Api.Remote.RemoteEndpoints.Denied(denial);
            var (result, batch) = await commands.StartServiceAsync(id, ct);
            return result.Succeeded ? Results.Ok(new { lote = batch }) : ToResult(result);
        }).RequireAuthorization(PermissionPolicies.ScriptsRun);

        api.MapGet("/reglas-alerta", (AlertQueries q, CancellationToken ct) => q.RulesAsync(ct))
            .RequireAuthorization(PermissionPolicies.AlertsView);
        var rules = api.MapGroup("/reglas-alerta").RequireAuthorization(PermissionPolicies.AlertsManage);
        rules.MapPut("/", async (RuleInput input, IValidator<RuleInput> validator, AlertCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.SaveRuleAsync(input, ct)));
        rules.MapDelete("/{id:int}", async (int id, AlertCommands commands, CancellationToken ct) =>
            ToResult(await commands.DeleteRuleAsync(id, ct)));
    }

    /// <summary>Alertas (decisión 83): 10 por página, un filtro por campo y rango de fechas de creación.</summary>
    private static async Task<IResult> ListAsync(
        AlertQueries queries,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search = null,
        string? estado = null,
        string? nivel = null,
        int? sucursal = null,
        int? maquina = null,
        int? dispositivo = null,
        bool? red = null,
        string? origen = null,
        string? codigo = null,
        int? atendidaPor = null,
        bool? automatica = null,
        DateTime? fechaDesde = null,
        DateTime? fechaHasta = null,
        int? page = null,
        int? limit = null)
    {
        if (Paging.DateRangeError(fechaDesde, fechaHasta) is { } error)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["fechaHasta"] = [error] });
        var (p, l) = Paging.Normalize(page, limit);
        return Results.Ok(await queries.AlertsAsync(new AlertFilter
        {
            Query = search, Status = estado, Level = nivel, BranchId = sucursal, MachineId = maquina, DeviceId = dispositivo,
            Network = red, Source = origen, Code = codigo, HandledById = atendidaPor, Automatic = automatica,
            From = fechaDesde, To = fechaHasta, Page = p, Limit = l,
        }, ct));
    }
}

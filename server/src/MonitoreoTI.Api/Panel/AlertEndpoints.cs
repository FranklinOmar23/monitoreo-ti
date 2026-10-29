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

        api.MapGet("/reglas-alerta", (AlertQueries q, CancellationToken ct) => q.RulesAsync(ct))
            .RequireAuthorization(PermissionPolicies.AlertsView);
        var rules = api.MapGroup("/reglas-alerta").RequireAuthorization(PermissionPolicies.AlertsManage);
        rules.MapPut("/", async (RuleInput input, IValidator<RuleInput> validator, AlertCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.SaveRuleAsync(input, ct)));
        rules.MapDelete("/{id:int}", async (int id, AlertCommands commands, CancellationToken ct) =>
            ToResult(await commands.DeleteRuleAsync(id, ct)));
    }

    private static Task<AlertList> ListAsync(
        AlertQueries queries,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search = null,
        string? estado = null,
        string? nivel = null,
        int? sucursal = null,
        int? maquina = null,
        int pagina = 1,
        int tamano = 50) =>
        queries.AlertsAsync(new AlertFilter(search, estado, nivel, sucursal, maquina, pagina, tamano), ct);
}

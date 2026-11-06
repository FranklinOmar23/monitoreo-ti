using FluentValidation;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Api.Panel;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Remote;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Remote;

/// <summary>
/// Biblioteca de scripts (W-33) y ejecuciones (W-32). Ver: scripts.ejecutar o scripts.editar; editar la biblioteca:
/// scripts.editar; ejecutar: scripts.ejecutar, con el módulo remoto en el plan y un 2FA de menos de 8 horas.
/// </summary>
public static class RemoteEndpoints
{
    public static void MapRemoteEndpoints(this IEndpointRouteBuilder app)
    {
        var view = app.MapGroup("/api/v1/scripts").RequireAuthorization(PermissionPolicies.ScriptsView);
        // Biblioteca paginada; los selectores (ejecutar, tareas) usan /opciones con todos.
        view.MapGet("/", async (RemoteCommands r, HttpRequest request, CancellationToken ct) => MonitoreoTI.Api.Panel.ListPaging.Ok(await r.ScriptsAsync(ct), request));
        view.MapGet("/opciones", async (RemoteCommands r, CancellationToken ct) => Results.Ok(await r.ScriptsAsync(ct)));

        var edit = app.MapGroup("/api/v1/scripts").RequireAuthorization(PermissionPolicies.ScriptsEdit);
        edit.MapPost("/", async (ScriptInput input, IValidator<ScriptInput> v, RemoteCommands r, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await r.SaveScriptAsync(null, input, ct), created: true));
        edit.MapPut("/{id:int}", async (int id, ScriptInput input, IValidator<ScriptInput> v, RemoteCommands r, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await r.SaveScriptAsync(id, input, ct)));
        edit.MapDelete("/{id:int}", async (int id, RemoteCommands r, CancellationToken ct) => ToResult(await r.DeleteScriptAsync(id, ct)));

        var sessions = app.MapGroup("/api/v1/sesiones-remotas").RequireAuthorization(PermissionPolicies.RemoteAny);
        // Historial de sesiones (decisión 83): 10 por página, filtros por campo y rango de fechas de inicio.
        sessions.MapGet("/", async (RemoteSessions s, CancellationToken ct, int? maquina, int? usuario, string? tipo, bool? soloVer,
            bool? activa, DateTime? fechaDesde, DateTime? fechaHasta, int? page, int? limit) =>
        {
            if (MonitoreoTI.Application.Panel.Paging.DateRangeError(fechaDesde, fechaHasta) is { } error)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["fechaHasta"] = [error] });
            var (p, l) = MonitoreoTI.Application.Panel.Paging.Normalize(page, limit);
            return Results.Ok(new { data = await s.ListAsync(new SessionFilter(maquina, usuario, tipo, soloVer, activa, fechaDesde, fechaHasta, p, l), ct) });
        });
        sessions.MapPost("/", async (SessionInput input, IValidator<SessionInput> v, RemoteAccessGuard guard, RemoteSessions s, HttpContext http, CancellationToken ct) =>
        {
            if (await ValidateAsync(input, v, ct) is { } invalid)
                return invalid;
            if (await guard.CheckAsync(ct) is { } denial)
                return Denied(denial);
            var (ticket, error) = await s.OpenAsync(input, http.Connection.RemoteIpAddress?.ToString(), ct);
            return error is not null ? ToResult(error) : Results.Created($"/api/v1/sesiones-remotas/{ticket!.SessionId}", ticket);
        });
        // Si el usuario del equipo debe aceptar antes de ver su pantalla (por empresa, decisión 52).
        sessions.MapGet("/configuracion", async (MonitoreoTI.Infrastructure.Persistence.MonitoreoDbContext db, MonitoreoTI.Application.Panel.ICurrentUser user, CancellationToken ct) =>
        {
            var companyId = user.CompanyId;
            return Results.Ok(new RemoteSettings(await db.Companies.Where(c => c.Id == companyId).Select(c => c.AskRemoteConsent).FirstOrDefaultAsync(ct)));
        });
        app.MapPut("/api/v1/sesiones-remotas/configuracion", async (RemoteSettings input, MonitoreoTI.Infrastructure.Persistence.MonitoreoDbContext db,
            MonitoreoTI.Application.Panel.ICurrentUser user, MonitoreoTI.Application.Panel.IAuditTrail audit, CancellationToken ct) =>
        {
            var companyId = user.CompanyId;
            await db.Companies.Where(c => c.Id == companyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AskRemoteConsent, input.PedirPermiso), ct);
            await audit.RecordAsync("remoto.configuracion", "empresas", companyId.ToString(), null, new { pedirPermiso = input.PedirPermiso }, ct);
            return Results.Ok(input);
        }).RequireAuthorization(PermissionPolicies.UsersAdmin);
        sessions.MapPost("/{id:long}/cerrar", async (long id, RemoteSessions s, CancellationToken ct) => ToResult(await s.CloseAsync(id, ct)));

        // Tareas programadas (6.5): mismo permiso que ejecutar; guardar exige el módulo remoto y el 2FA reciente.
        var tasks = app.MapGroup("/api/v1/tareas-programadas").RequireAuthorization(PermissionPolicies.ScriptsRun);
        tasks.MapGet("/", async (ScheduledTasks t, HttpRequest request, CancellationToken ct) => MonitoreoTI.Api.Panel.ListPaging.Ok(await t.ListAsync(ct), request));
        tasks.MapPost("/", async (ScheduledTaskInput input, IValidator<ScheduledTaskInput> v, RemoteAccessGuard guard, ScheduledTasks t, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? (await guard.CheckAsync(ct) is { } denial ? Denied(denial) : ToResult(await t.SaveAsync(null, input, ct), created: true)));
        tasks.MapPut("/{id:int}", async (int id, ScheduledTaskInput input, IValidator<ScheduledTaskInput> v, RemoteAccessGuard guard, ScheduledTasks t, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? (await guard.CheckAsync(ct) is { } denial ? Denied(denial) : ToResult(await t.SaveAsync(id, input, ct))));
        tasks.MapDelete("/{id:int}", async (int id, ScheduledTasks t, CancellationToken ct) => ToResult(await t.DeleteAsync(id, ct)));

        // Actualización del agente (6.6), junto a los instaladores.
        var updates = app.MapGroup("/api/v1/actualizaciones").RequireAuthorization(PermissionPolicies.InstallersGenerate);
        updates.MapGet("/", async (AgentUpdater u, MonitoreoTI.Infrastructure.Persistence.MonitoreoDbContext db, MonitoreoTI.Application.Panel.ICurrentUser user, CancellationToken ct) =>
            Results.Ok(await u.StatusAsync(db, user, ct)));
        updates.MapPut("/automatica", async (AutoUpdateInput input, MonitoreoTI.Infrastructure.Persistence.MonitoreoDbContext db,
            MonitoreoTI.Application.Panel.ICurrentUser user, MonitoreoTI.Application.Panel.IAuditTrail audit, CancellationToken ct) =>
        {
            var companyId = user.CompanyId;
            await db.Companies.Where(c => c.Id == companyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AutoUpdateAgents, input.Activa), ct);
            await audit.RecordAsync("actualizacion.automatica", "empresas", companyId.ToString(), null, new { activa = input.Activa }, ct);
            return Results.Ok(input);
        });
        updates.MapPost("/desplegar", async (AgentUpdater u, MonitoreoTI.Application.Panel.ICurrentUser user, MonitoreoTI.Application.Panel.IAuditTrail audit, CancellationToken ct) =>
        {
            var offered = await u.RolloutAsync(user.CompanyId, onlyIfAutomatic: false, ct);
            await audit.RecordAsync("actualizacion.desplegar", "empresas", user.CompanyId.ToString(), null, new { equipos = offered }, ct);
            return Results.Ok(new { ofrecidos = offered });
        });

        var run = app.MapGroup("/api/v1/ejecuciones").RequireAuthorization(PermissionPolicies.ScriptsRun);
        run.MapGet("/", async (RemoteCommands r, HttpRequest request, CancellationToken ct, int? maquina) =>
            MonitoreoTI.Api.Panel.ListPaging.Ok(await r.RunsAsync(maquina, ct), request));
        run.MapGet("/{batchId:guid}", async (Guid batchId, RemoteCommands r, CancellationToken ct) =>
            await r.RunAsync(batchId, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());
        run.MapPost("/", async (RunInput input, IValidator<RunInput> v, RemoteAccessGuard guard, RemoteCommands r, CancellationToken ct) =>
        {
            if (await ValidateAsync(input, v, ct) is { } invalid)
                return invalid;
            if (await guard.CheckAsync(ct) is { } denial)
                return Denied(denial);
            var (result, error) = await r.RunAsync(input, ct);
            return error is not null ? ToResult(error) : Results.Created($"/api/v1/ejecuciones/{result!.BatchId}", result);
        });
    }

    /// <summary>403 con un código estable: el panel pide el 2FA o explica que el plan no lo incluye.</summary>
    public static IResult Denied(RemoteAccessDenial denial) => denial switch
    {
        RemoteAccessDenial.TwoFactorRequired => Results.Problem(
            title: "Por seguridad, confirme su código de verificación en dos pasos.", statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["codigo"] = "2fa_requerido" }),
        _ => Results.Problem(
            title: "El plan de su empresa no incluye el acceso remoto.", statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["codigo"] = "modulo_no_incluido" }),
    };
}

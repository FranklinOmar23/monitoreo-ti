using FluentValidation;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Panel;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>Calendario de mantenimiento (W-27): ver con permiso de tickets; programar y marcar hecho con tickets.gestionar.</summary>
public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1/mantenimientos").RequireAuthorization(PermissionPolicies.TicketsView);
        read.MapGet("/", async (Maintenances m, CancellationToken ct, DateOnly? desde, DateOnly? hasta, int? sucursal, int? maquina) =>
        {
            var from = desde ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-31);
            var to = hasta ?? from.AddDays(62);
            if (to < from || to.DayNumber - from.DayNumber > 400)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["hasta"] = ["El rango va de 1 a 400 días."] });
            return Results.Ok(await m.ListAsync(new MaintenanceFilter(from, to, sucursal, maquina), ct));
        });

        var edit = app.MapGroup("/api/v1/mantenimientos").RequireAuthorization(PermissionPolicies.TicketsManage);
        edit.MapPost("/", async (MaintenanceInput input, IValidator<MaintenanceInput> v, Maintenances m, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await m.SaveAsync(null, input, ct), created: true));
        edit.MapPut("/{id:int}", async (int id, MaintenanceInput input, IValidator<MaintenanceInput> v, Maintenances m, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await m.SaveAsync(id, input, ct)));
        edit.MapPost("/{id:int}/realizado", async (int id, MaintenanceDoneInput input, IValidator<MaintenanceDoneInput> v, Maintenances m, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await m.CompleteAsync(id, input, ct)));
        edit.MapDelete("/{id:int}", async (int id, Maintenances m, CancellationToken ct) => ToResult(await m.DeleteAsync(id, ct)));
    }
}

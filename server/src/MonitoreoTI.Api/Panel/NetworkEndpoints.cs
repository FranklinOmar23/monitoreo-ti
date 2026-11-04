using FluentValidation;
using MonitoreoTI.Application.Network;
using MonitoreoTI.Infrastructure.Network;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Red y data center (Fase 4): salas, gabinetes, catálogo, dispositivos, puertos, rosetas, cableado, ruta, historial,
/// importación y lo que sale del recolector SNMP (resumen, equipos desconocidos, búsqueda, diferencias).
/// Ver: red.ver; cambiar: red.editar.
/// </summary>
public static class NetworkEndpoints
{
    public static void MapNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        var read = app.MapGroup("/api/v1/red").RequireAuthorization(PermissionPolicies.NetworkView);
        read.MapGet("/salas", (NetworkQueries q, CancellationToken ct) => q.RoomsAsync(ct));
        read.MapGet("/gabinetes", (NetworkQueries q, CancellationToken ct, int? sala) => q.RacksAsync(sala, ct));
        read.MapGet("/gabinetes/{id:int}", async (int id, NetworkQueries q, CancellationToken ct) =>
            await q.RackAsync(id, ct) is { } rack ? Results.Ok(rack) : Results.NotFound());
        read.MapGet("/modelos", (NetworkQueries q, CancellationToken ct) => q.ModelsAsync(ct));
        read.MapGet("/dispositivos", (NetworkQueries q, CancellationToken ct,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search, string? tipo, int? sucursal, int? sala, int? gabinete) =>
            q.DevicesAsync(new DeviceFilter(search, tipo, sucursal, sala, gabinete), ct));
        read.MapGet("/dispositivos/{id:int}", async (int id, NetworkQueries q, CancellationToken ct) =>
            await q.DeviceAsync(id, ct) is { } device ? Results.Ok(device) : Results.NotFound());
        read.MapGet("/rosetas", (NetworkQueries q, CancellationToken ct, int? sucursal,
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search) => q.OutletsAsync(sucursal, search, ct));
        read.MapGet("/cables", (NetworkQueries q, CancellationToken ct, int? dispositivo) => q.CablesAsync(dispositivo, ct));
        read.MapGet("/ruta", async (NetworkQueries q, CancellationToken ct, int? roseta, int? puerto) =>
            await q.PathAsync(roseta, puerto, ct) is { } path ? Results.Ok(path) : Results.NotFound());
        // Recolector y correlación (Fase 4.2 y 4.3).
        read.MapGet("/resumen", (NetworkInsights i, CancellationToken ct) => i.SummaryAsync(ct));
        read.MapGet("/recolectores", (NetworkInsights i, CancellationToken ct, int? sucursal) => i.CollectorsAsync(sucursal, ct));
        read.MapGet("/desconocidos", (NetworkInsights i, CancellationToken ct) => i.UnknownAsync(ct));
        read.MapGet("/topologia", (NetworkInsights i, CancellationToken ct) => i.TopologyAsync(ct));
        read.MapGet("/diferencias", (NetworkInsights i, CancellationToken ct) => i.DifferencesAsync(ct));
        read.MapGet("/buscar", (NetworkInsights i, CancellationToken ct, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search) =>
            i.SearchAsync(search ?? "", ct));
        read.MapGet("/historial", (NetworkQueries q, CancellationToken ct, string? entidad, string? id, int? dias, int? pagina) =>
            q.HistoryAsync(new HistoryFilter(entidad, id, dias ?? 30, pagina ?? 1), ct));

        var edit = app.MapGroup("/api/v1/red").RequireAuthorization(PermissionPolicies.NetworkEdit);
        edit.MapPost("/salas", async (RoomInput input, IValidator<RoomInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveRoomAsync(null, input, ct), created: true));
        edit.MapPut("/salas/{id:int}", async (int id, RoomInput input, IValidator<RoomInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveRoomAsync(id, input, ct)));
        edit.MapDelete("/salas/{id:int}", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.DeleteRoomAsync(id, ct)));

        edit.MapPost("/gabinetes", async (RackInput input, IValidator<RackInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveRackAsync(null, input, ct), created: true));
        edit.MapPut("/gabinetes/{id:int}", async (int id, RackInput input, IValidator<RackInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveRackAsync(id, input, ct)));
        edit.MapDelete("/gabinetes/{id:int}", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.DeleteRackAsync(id, ct)));

        edit.MapPost("/modelos", async (DeviceModelInput input, IValidator<DeviceModelInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveModelAsync(null, input, ct), created: true));
        edit.MapPut("/modelos/{id:int}", async (int id, DeviceModelInput input, IValidator<DeviceModelInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveModelAsync(id, input, ct)));

        edit.MapPost("/dispositivos", async (DeviceInput input, IValidator<DeviceInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.CreateDeviceAsync(input, ct), created: true));
        edit.MapPut("/dispositivos/{id:int}", async (int id, DeviceInput input, IValidator<DeviceInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.UpdateDeviceAsync(id, input, ct)));
        edit.MapPut("/dispositivos/{id:int}/ubicacion", async (int id, DevicePlacementInput input, NetworkCommands c, CancellationToken ct) =>
            ToResult(await c.PlaceDeviceAsync(id, input, ct)));
        edit.MapPost("/dispositivos/{id:int}/baja", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.RetireDeviceAsync(id, ct)));

        edit.MapPut("/puertos/{id:int}", async (int id, PortInput input, IValidator<PortInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.UpdatePortAsync(id, input, ct)));

        edit.MapPut("/puertos/{id:int}/asignacion", async (int id, PortAssignmentInput input, NetworkCommands c, CancellationToken ct) =>
            ToResult(await c.AssignPortAsync(id, input, ct)));

        edit.MapPost("/rosetas", async (OutletInput input, IValidator<OutletInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveOutletAsync(null, input, ct), created: true));
        edit.MapPut("/rosetas/{id:int}", async (int id, OutletInput input, IValidator<OutletInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.SaveOutletAsync(id, input, ct)));
        edit.MapDelete("/rosetas/{id:int}", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.DeleteOutletAsync(id, ct)));

        edit.MapPost("/cables", async (CableInput input, IValidator<CableInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.CreateCableAsync(input, ct), created: true));
        // Importación masiva (W-68): la vista previa valida sin guardar; importar guarda todo o nada.
        edit.MapPost("/importar/vista-previa", async (ImportInput input, IValidator<ImportInput> v, NetworkImport i, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? Results.Ok(await i.RunAsync(input, apply: false, ct)));
        edit.MapPost("/importar", async (ImportInput input, IValidator<ImportInput> v, NetworkImport i, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? Results.Ok(await i.RunAsync(input, apply: true, ct)));

        edit.MapDelete("/cables/{id:int}", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.DeleteCableAsync(id, ct)));
    }
}

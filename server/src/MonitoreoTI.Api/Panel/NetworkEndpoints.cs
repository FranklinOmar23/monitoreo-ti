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
        // Direcciones IP (decisión 72).
        read.MapGet("/subredes", (IpAddresses ips, CancellationToken ct, int? sucursal) => ips.SubnetsAsync(sucursal, ct));
        read.MapGet("/subredes/{id:int}", async (int id, IpAddresses ips, CancellationToken ct) =>
            await ips.SubnetAsync(id, ct) is { } subnet ? Results.Ok(subnet) : Results.NotFound());
        read.MapGet("/subredes/{id:int}/instalaciones", async (int id, RemoteInstalls installs, CancellationToken ct) =>
            await installs.ListAsync(id, ct) is { } list ? Results.Ok(list) : Results.NotFound());
        read.MapGet("/subredes/{id:int}/historial", async (int id, string ip, IpAddresses ips, CancellationToken ct, int? dias) =>
            await ips.HistoryAsync(id, ip, dias ?? 30, ct) is { } history ? Results.Ok(history) : Results.NotFound());
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
        edit.MapPost("/dispositivos/{id:int}/sondear", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.PollNowAsync(id, ct)));
        edit.MapPost("/dispositivos/{id:int}/probar-api", async (int id, ApiTestInput input, IValidator<ApiTestInput> v, NetworkCommands c, CancellationToken ct) =>
        {
            if (await ValidateAsync(input, v, ct) is { } invalid)
                return invalid;
            var (error, result) = await c.TestApiAsync(id, input, ct);
            return error is not null ? ToResult(error) : Results.Ok(result);
        });
        edit.MapPost("/dispositivos/{id:int}/baja", async (int id, NetworkCommands c, CancellationToken ct) => ToResult(await c.RetireDeviceAsync(id, ct)));

        edit.MapPut("/puertos/{id:int}", async (int id, PortInput input, IValidator<PortInput> v, NetworkCommands c, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await c.UpdatePortAsync(id, input, ct)));

        edit.MapPut("/puertos/{id:int}/asignacion", async (int id, PortAssignmentInput input, NetworkCommands c, CancellationToken ct) =>
            ToResult(await c.AssignPortAsync(id, input, ct)));

        edit.MapPost("/subredes", async (SubnetInput input, IValidator<SubnetInput> v, IpAddresses ips, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await ips.SaveSubnetAsync(null, input, ct), created: true));
        edit.MapPut("/subredes/{id:int}", async (int id, SubnetInput input, IValidator<SubnetInput> v, IpAddresses ips, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await ips.SaveSubnetAsync(id, input, ct)));
        edit.MapDelete("/subredes/{id:int}", async (int id, IpAddresses ips, CancellationToken ct) => ToResult(await ips.DeleteSubnetAsync(id, ct)));
        edit.MapPost("/subredes/{id:int}/reservas", async (int id, IpReservationInput input, IValidator<IpReservationInput> v, IpAddresses ips, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? ToResult(await ips.AddReservationAsync(id, input, ct), created: true));
        edit.MapDelete("/subredes/{id:int}/reservas/{reserva:int}", async (int id, int reserva, IpAddresses ips, CancellationToken ct) =>
            ToResult(await ips.DeleteReservationAsync(id, reserva, ct)));
        // Instalar el agente en equipos de la subred (decisión 77): permiso de instaladores, módulo remoto y 2FA reciente.
        app.MapPost("/api/v1/red/subredes/{id:int}/instalar-agente", async (
            int id, RemoteInstallInput input, IValidator<RemoteInstallInput> v, MonitoreoTI.Infrastructure.Remote.RemoteAccessGuard guard,
            RemoteInstalls installs, HttpContext http, CancellationToken ct) =>
        {
            if (await ValidateAsync(input, v, ct) is { } invalid)
                return invalid;
            if (await guard.CheckAsync(ct) is { } denial)
                return MonitoreoTI.Api.Remote.RemoteEndpoints.Denied(denial);
            return ToResult(await installs.StartAsync(id, input, $"{http.Request.Scheme}://{http.Request.Host}", ct), created: true);
        }).RequireAuthorization(PermissionPolicies.InstallersGenerate);

        edit.MapPost("/subredes/{id:int}/escanear", async (int id, IpAddresses ips, CancellationToken ct) =>
            ToResult(await ips.RequestSweepAsync(id, ct)));

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

using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Network;
using MonitoreoTI.Infrastructure.Panel;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// API de lectura del panel y la app (/api/v1). Cada grupo exige su permiso. Un recurso de otra empresa
/// o de una sucursal no asignada responde 404, igual que uno inexistente: no se revela que existe.
/// </summary>
public static class PanelEndpoints
{
    public static void MapPanelEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/dashboard", (PanelQueries q, CancellationToken ct) => q.DashboardAsync(ct))
            .RequireAuthorization(PermissionPolicies.DashboardView);

        // Enlaces monitoreoti://q/{codigo} del panel y escáner de la app: qué es y su id.
        api.MapGet("/qr/{codigo:guid}", async (Guid codigo, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.ResolveQrAsync(codigo, ct)))
            .RequireAuthorization();

        var machines = api.MapGroup("/maquinas").RequireAuthorization(PermissionPolicies.MachinesView);
        machines.MapGet("/", ListMachinesAsync);
        machines.MapGet("/{id:int}", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.MachineAsync(id, ct)));
        machines.MapGet("/{id:int}/metricas", MetricsAsync);
        machines.MapGet("/{id:int}/software", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.SoftwareAsync(id, ct)));
        machines.MapGet("/{id:int}/interfaces", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.InterfacesAsync(id, ct)));
        machines.MapGet("/{id:int}/eventos", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.ErrorsAsync(id, ct)));
        machines.MapGet("/{id:int}/procesos", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.ProcessesAsync(id, ct)));
        machines.MapGet("/{id:int}/servicios", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.ServicesAsync(id, ct)));
        // "Conexión de red" de la ficha: switch, puerto y VLAN donde lo vio el recolector (o donde lo asignaron a mano).
        machines.MapGet("/{id:int}/conexion-red", async (int id, PanelQueries q, NetworkInsights network, CancellationToken ct) =>
            await q.MachineAsync(id, ct) is null ? Results.NotFound() : Results.Ok(new { connection = await network.MachineLocationAsync(id, ct) }));
        machines.MapGet("/{id:int}/historial", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.HistoryAsync(id, ct)));
        machines.MapGet("/{id:int}/qr", async (int id, PanelQueries q, CancellationToken ct) =>
        {
            var machine = await q.MachineAsync(id, ct);
            if (machine is null)
                return Results.NotFound();
            var content = QrImages.MachineContent(machine.QrCode);
            return Results.Ok(new { codigo = machine.QrCode, contenido = content, png = QrImages.PngDataUri(content, 8) });
        });

        var branches = api.MapGroup("/sucursales").RequireAuthorization(PermissionPolicies.MachinesView);
        branches.MapGet("/", (PanelQueries q, CancellationToken ct) => q.BranchesAsync(ct));
        branches.MapGet("/{id:int}", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.BranchAsync(id, ct)));
    }

    private static Task<MachineList> ListMachinesAsync(
        PanelQueries queries,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search = null,
        int? sucursal = null,
        string? tipo = null,
        string? estado = null,
        string? so = null,
        int pagina = 1,
        int tamano = 50) =>
        queries.MachinesAsync(new MachineFilter(search, sucursal, tipo, estado, so, pagina, tamano), ct);

    private static async Task<IResult> MetricsAsync(int id, PanelQueries q, CancellationToken ct, string rango = "24h")
    {
        MetricRange? range = rango switch
        {
            "1h" => MetricRange.LastHour,
            "24h" => MetricRange.LastDay,
            "7d" => MetricRange.LastWeek,
            _ => null,
        };
        if (range is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rango"] = ["Use 1h, 24h o 7d."] });

        return OkOrNotFound(await q.MetricsAsync(id, range.Value, ct));
    }

    private static IResult OkOrNotFound<T>(T? value) => value is null ? Results.NotFound() : Results.Ok(value);
}

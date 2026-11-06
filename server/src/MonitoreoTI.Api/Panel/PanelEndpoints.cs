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
        // Para listas desplegables: todos los equipos visibles (id y nombre), sin paginar.
        machines.MapGet("/opciones", (PanelQueries q, CancellationToken ct, int? sucursal) => q.MachineOptionsAsync(sucursal, ct));
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
        // Decisión 83: la pantalla de sucursales va paginada; los desplegables usan /opciones (todas).
        branches.MapGet("/", async (PanelQueries q, HttpRequest request, CancellationToken ct) => ListPaging.Ok(await q.BranchesAsync(ct), request));
        branches.MapGet("/opciones", (PanelQueries q, CancellationToken ct) => q.BranchesAsync(ct));
        branches.MapGet("/{id:int}", async (int id, PanelQueries q, CancellationToken ct) => OkOrNotFound(await q.BranchAsync(id, ct)));
    }

    /// <summary>Equipos (decisión 83): 10 por página, un filtro por campo y rango de fechas de alta.</summary>
    private static async Task<IResult> ListMachinesAsync(
        PanelQueries queries,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search = null,
        string? nombre = null,
        int? sucursal = null,
        int? departamento = null,
        string? tipo = null,
        string? estado = null,
        string? so = null,
        string? marca = null,
        string? modelo = null,
        string? serie = null,
        string? ip = null,
        string? mac = null,
        string? responsable = null,
        DateTime? fechaDesde = null,
        DateTime? fechaHasta = null,
        int? page = null,
        int? limit = null)
    {
        if (Paging.DateRangeError(fechaDesde, fechaHasta) is { } error)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["fechaHasta"] = [error] });
        var (p, l) = Paging.Normalize(page, limit);
        return Results.Ok(await queries.MachinesAsync(new MachineFilter
        {
            Query = search, Name = nombre, BranchId = sucursal, DepartmentId = departamento, Type = tipo, Status = estado,
            OperatingSystem = so, Brand = marca, Model = modelo, SerialNumber = serie, IpAddress = ip, MacAddress = mac,
            AssignedTo = responsable, From = fechaDesde, To = fechaHasta, Page = p, Limit = l,
        }, ct));
    }

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

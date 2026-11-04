using MonitoreoTI.Application.Reports;
using MonitoreoTI.Infrastructure.Reports;

namespace MonitoreoTI.Api.Panel;

/// <summary>Reportes (W-35) con el permiso reportes.ver; los avanzados, además, con el módulo reportes_avanzados del plan.</summary>
public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/v1/reportes").RequireAuthorization(PermissionPolicies.ReportsView);

        reports.MapGet("/", async (ReportQueries r, CancellationToken ct) => new ReportsInfo(await r.AdvancedAsync(ct)));

        reports.MapGet("/inventario", (ReportQueries r, CancellationToken ct, int? sucursal, int? departamento) =>
            Basic(Filter(null, null, sucursal, departamento), f => r.InventoryAsync(f, ct)));
        reports.MapGet("/disponibilidad", (ReportQueries r, CancellationToken ct, DateOnly? desde, DateOnly? hasta, int? sucursal, int? departamento) =>
            Basic(Filter(desde, hasta, sucursal, departamento), f => r.AvailabilityAsync(f, ct)));
        reports.MapGet("/alertas", (ReportQueries r, CancellationToken ct, DateOnly? desde, DateOnly? hasta, int? sucursal, int? departamento) =>
            Basic(Filter(desde, hasta, sucursal, departamento), f => r.AlertsAsync(f, ct)));
        reports.MapGet("/discos-seguridad", (ReportQueries r, CancellationToken ct, int? sucursal, int? departamento) =>
            Basic(Filter(null, null, sucursal, departamento), f => r.DiskSecurityAsync(f, ct)));

        reports.MapGet("/rendimiento", (ReportQueries r, CancellationToken ct, DateOnly? desde, DateOnly? hasta, int? sucursal, int? departamento) =>
            AdvancedAsync(r, Filter(desde, hasta, sucursal, departamento), f => r.PerformanceAsync(f, ct), ct));
        reports.MapGet("/software", (ReportQueries r, CancellationToken ct, string? buscar, int? sucursal, int? departamento) =>
            AdvancedAsync(r, Filter(null, null, sucursal, departamento), f => r.SoftwareAsync(f, buscar, ct), ct));
        reports.MapGet("/tickets", (ReportQueries r, CancellationToken ct, DateOnly? desde, DateOnly? hasta, int? sucursal, int? departamento) =>
            AdvancedAsync(r, Filter(desde, hasta, sucursal, departamento), f => r.TicketsAsync(f, ct), ct));
        reports.MapGet("/remoto", (ReportQueries r, CancellationToken ct, DateOnly? desde, DateOnly? hasta, int? sucursal, int? departamento) =>
            AdvancedAsync(r, Filter(desde, hasta, sucursal, departamento), f => r.RemoteAsync(f, ct), ct));
    }

    /// <summary>Por omisión, los últimos 30 días (hoy incluido). Null si el rango no es válido.</summary>
    private static ReportFilter? Filter(DateOnly? desde, DateOnly? hasta, int? sucursal, int? departamento)
    {
        var to = hasta ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = desde ?? to.AddDays(-29);
        return to < from || to.DayNumber - from.DayNumber >= ReportQueries.MaxDays
            ? null
            : new ReportFilter(from, to, sucursal, departamento);
    }

    private static IResult InvalidRange() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["hasta"] = [$"El periodo va de 1 a {ReportQueries.MaxDays} días."] });

    private static async Task<IResult> Basic<T>(ReportFilter? filter, Func<ReportFilter, Task<T>> query) =>
        filter is null ? InvalidRange() : Results.Ok(await query(filter));

    private static async Task<IResult> AdvancedAsync<T>(ReportQueries reports, ReportFilter? filter, Func<ReportFilter, Task<T>> query, CancellationToken ct)
    {
        if (!await reports.AdvancedAsync(ct))
        {
            return Results.Problem(
                title: "Este reporte es parte de los reportes avanzados, que no incluye el plan de la empresa.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["codigo"] = "modulo_no_incluido" });
        }
        return await Basic(filter, query);
    }
}

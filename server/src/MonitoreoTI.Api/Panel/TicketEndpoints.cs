using FluentValidation;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Panel;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Tickets (W-24 a W-26). Ver, crear, comentar y adjuntar: tickets.crear o tickets.gestionar (el alcance lo decide
/// TicketQueries). Asignar y cambiar estado: tickets.gestionar.
/// </summary>
public static class TicketEndpoints
{
    public static void MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var tickets = app.MapGroup("/api/v1/tickets").RequireAuthorization(PermissionPolicies.TicketsView);
        tickets.MapGet("/", ListAsync);
        tickets.MapGet("/{id:int}", async (int id, TicketQueries q, CancellationToken ct) =>
            await q.TicketAsync(id, ct) is { } ticket ? Results.Ok(ticket) : Results.NotFound());
        tickets.MapGet("/tecnicos", (TicketQueries q, CancellationToken ct) => q.TechniciansAsync(ct));
        tickets.MapGet("/{id:int}/adjuntos/{fileId:int}", async (int id, int fileId, TicketQueries q, CancellationToken ct) =>
            await q.AttachmentAsync(id, fileId, ct) is { } file ? Results.Ok(file) : Results.NotFound());

        tickets.MapPost("/", async (TicketInput input, IValidator<TicketInput> validator, TicketCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.CreateAsync(input, ct), created: true));
        tickets.MapPut("/{id:int}", async (int id, TicketUpdateInput input, IValidator<TicketUpdateInput> validator, TicketCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.UpdateAsync(id, input, ct)));
        tickets.MapPost("/{id:int}/comentarios", async (int id, TicketCommentInput input, IValidator<TicketCommentInput> validator, TicketCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.CommentAsync(id, input, ct), created: true));
        tickets.MapPost("/{id:int}/adjuntos", async (int id, TicketAttachmentInput input, IValidator<TicketAttachmentInput> validator, TicketCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.AttachAsync(id, input, ct), created: true));

        var manage = app.MapGroup("/api/v1/tickets").RequireAuthorization(PermissionPolicies.TicketsManage);
        manage.MapPost("/{id:int}/estado", async (int id, TicketStatusInput input, IValidator<TicketStatusInput> validator, TicketCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.ChangeStatusAsync(id, input, ct)));
        manage.MapPost("/{id:int}/asignar", async (int id, TicketAssignInput input, TicketCommands commands, CancellationToken ct) =>
            ToResult(await commands.AssignAsync(id, input, ct)));
    }

    /// <summary>Tickets (decisión 83): 10 por página, un filtro por campo y rango de fechas de apertura.</summary>
    private static async Task<IResult> ListAsync(
        TicketQueries queries,
        CancellationToken ct,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "q")] string? search = null,
        string? titulo = null,
        string? estado = null,
        string? prioridad = null,
        string? asignado = null,
        int? creadoPor = null,
        int? sucursal = null,
        int? maquina = null,
        long? alerta = null,
        DateTime? fechaDesde = null,
        DateTime? fechaHasta = null,
        int? page = null,
        int? limit = null)
    {
        if (Paging.DateRangeError(fechaDesde, fechaHasta) is { } error)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["fechaHasta"] = [error] });
        var (p, l) = Paging.Normalize(page, limit);
        return Results.Ok(await queries.TicketsAsync(new TicketFilter
        {
            Query = search, Title = titulo, Status = estado, Priority = prioridad, Assignee = asignado, CreatedById = creadoPor,
            BranchId = sucursal, MachineId = maquina, AlertId = alerta, From = fechaDesde, To = fechaHasta, Page = p, Limit = l,
        }, ct));
    }
}

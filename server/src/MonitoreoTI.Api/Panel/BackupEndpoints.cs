using FluentValidation;
using MonitoreoTI.Api.Remote;
using MonitoreoTI.Application.Backups;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Infrastructure.Backups;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Respaldos de servidores (decisión 82). Verlos pide equipos.ver; configurarlos o lanzarlos, respaldos.gestionar y un
/// 2FA reciente (detienen servicios y sacan datos del servidor).
/// </summary>
public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var path = "/api/v1/maquinas/{id:int}/respaldos";
        app.MapGet(path, async (int id, Backups backups, CancellationToken ct) =>
            await backups.OverviewAsync(id, ct) is { } overview ? Results.Ok(overview) : Results.NotFound())
            .RequireAuthorization(PermissionPolicies.MachinesView);

        var manage = app.MapGroup(path).RequireAuthorization(PermissionPolicies.BackupsManage);
        manage.MapPost("/", async (int id, BackupJobInput input, IValidator<BackupJobInput> v, Backups backups, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? await TwoFactorAsync(backups, ct) ?? ToResult(await backups.SaveAsync(id, null, input, ct), created: true));
        manage.MapPut("/{trabajo:int}", async (int id, int trabajo, BackupJobInput input, IValidator<BackupJobInput> v, Backups backups, CancellationToken ct) =>
            await ValidateAsync(input, v, ct) ?? await TwoFactorAsync(backups, ct) ?? ToResult(await backups.SaveAsync(id, trabajo, input, ct)));
        manage.MapDelete("/{trabajo:int}", async (int id, int trabajo, Backups backups, CancellationToken ct) =>
            await TwoFactorAsync(backups, ct) ?? ToResult(await backups.DeleteAsync(id, trabajo, ct)));
        manage.MapPost("/{trabajo:int}/ahora", async (int id, int trabajo, BackupRunNowInput? input, Backups backups, CancellationToken ct) =>
            await TwoFactorAsync(backups, ct) ?? ToResult(await backups.RunNowAsync(id, trabajo, input?.Completo == true, ct)));

        // Lo que hay en el servidor para elegir: instancias y bases de SQL Server, servicios y discos externos.
        manage.MapPost("/explorar", async (int id, Backups backups, CancellationToken ct) =>
        {
            var (result, problem) = await backups.ExploreAsync(id, ct);
            return problem is not null ? Results.Problem(title: problem, statusCode: StatusCodes.Status409Conflict)
                : result is null ? Results.NotFound() : Results.Ok(result);
        });
        manage.MapPost("/probar-destino", async (int id, BackupDestinationInput input, IValidator<BackupDestinationInput> v, Backups backups, CancellationToken ct) =>
        {
            if (await ValidateAsync(input, v, ct) is { } invalid)
                return invalid;
            var (result, problem) = await backups.TestDestinationAsync(id, input, ct);
            return problem is not null ? Results.Problem(title: problem, statusCode: StatusCodes.Status409Conflict)
                : result is null ? Results.NotFound() : Results.Ok(result);
        });
    }

    private static async Task<IResult?> TwoFactorAsync(Backups backups, CancellationToken ct) =>
        await backups.RecentTwoFactorAsync(ct) ? null : RemoteEndpoints.Denied(RemoteAccessDenial.TwoFactorRequired);
}

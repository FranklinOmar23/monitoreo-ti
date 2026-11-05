using FluentValidation;
using MonitoreoTI.Application.Panel;
using MonitoreoTI.Infrastructure.Panel;

namespace MonitoreoTI.Api.Panel;

/// <summary>Escrituras del panel: equipos (W-13, W-14), sucursales (W-15 a W-17) e instaladores (W-18, W-19).</summary>
public static class PanelWriteEndpoints
{
    /// <summary>Nombre del instalador que descarga el panel.</summary>
    public const string InstallerFileName = "MonitoreoTI-Agente-Setup.exe";

    public static void MapPanelWriteEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // ---------------------------------------------------------------- equipos
        var machines = api.MapGroup("/maquinas").RequireAuthorization(PermissionPolicies.MachinesEdit);
        machines.MapPost("/", async (MachineInput input, IValidator<MachineInput> validator, PanelCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.CreateMachineAsync(input, ct), created: true));
        machines.MapPut("/{id:int}", async (int id, MachineInput input, IValidator<MachineInput> validator, PanelCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.UpdateMachineAsync(id, input, ct)));
        machines.MapPost("/{id:int}/baja", async (int id, PanelCommands commands, CancellationToken ct) =>
            ToResult(await commands.RetireMachineAsync(id, ct)));

        // QR en lote (W-14): solo los equipos que el usuario puede ver; el resto se ignora sin revelar si existen.
        api.MapGet("/maquinas/qr", async (string ids, PanelQueries queries, CancellationToken ct) =>
        {
            var requested = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(id => int.TryParse(id, out var value) ? value : 0).Where(id => id > 0).Distinct().Take(200).ToList();

            var labels = new List<object>();
            foreach (var id in requested)
            {
                var machine = await queries.MachineAsync(id, ct);
                if (machine is null)
                    continue;
                var content = QrImages.MachineContent(machine.QrCode);
                labels.Add(new { id, nombre = machine.Name, sucursal = machine.BranchName, codigo = machine.QrCode, png = QrImages.PngDataUri(content, 8) });
            }
            return Results.Ok(labels);
        }).RequireAuthorization(PermissionPolicies.MachinesView);

        api.MapGet("/departamentos", (PanelCommands commands, CancellationToken ct) => commands.DepartmentsAsync(ct))
            .RequireAuthorization(PermissionPolicies.MachinesView);

        // ---------------------------------------------------------------- sucursales
        var branches = api.MapGroup("/sucursales").RequireAuthorization(PermissionPolicies.BranchesEdit);
        branches.MapPost("/", async (BranchInput input, IValidator<BranchInput> validator, PanelCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.CreateBranchAsync(input, ct), created: true));
        branches.MapPut("/{id:int}", async (int id, BranchInput input, IValidator<BranchInput> validator, PanelCommands commands, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await commands.UpdateBranchAsync(id, input, ct)));
        api.MapGet("/usuarios/empresa", (PanelCommands commands, CancellationToken ct) => commands.CompanyUsersAsync(ct))
            .RequireAuthorization(PermissionPolicies.BranchesEdit);

        // ---------------------------------------------------------------- instaladores
        var installers = api.MapGroup("/instaladores").RequireAuthorization(PermissionPolicies.InstallersGenerate);
        installers.MapGet("/", (PanelCommands commands, CancellationToken ct) => commands.EnrollmentTokensAsync(ct));
        installers.MapPost("/", CreateTokenAsync);
        installers.MapGet("/{id:int}/comando", RevealTokenAsync);
        installers.MapPost("/{id:int}/revocar", async (int id, PanelCommands commands, CancellationToken ct) =>
            ToResult(await commands.RevokeEnrollmentTokenAsync(id, ct)));
        installers.MapDelete("/{id:int}", async (int id, PanelCommands commands, CancellationToken ct) =>
            ToResult(await commands.DeleteEnrollmentTokenAsync(id, ct)));
        installers.MapGet("/descarga", DownloadInstaller);

        var pending = api.MapGroup("/equipos-pendientes").RequireAuthorization(PermissionPolicies.InstallersGenerate);
        pending.MapGet("/", (PanelCommands commands, CancellationToken ct) => commands.PendingAgentsAsync(ct));
        pending.MapPost("/{agentId:int}/aprobar", async (int agentId, PanelCommands commands, CancellationToken ct) =>
            ToResult(await commands.DecidePendingAgentAsync(agentId, approve: true, ct)));
        pending.MapPost("/{agentId:int}/rechazar", async (int agentId, PanelCommands commands, CancellationToken ct) =>
            ToResult(await commands.DecidePendingAgentAsync(agentId, approve: false, ct)));
    }

    private static async Task<IResult> CreateTokenAsync(
        EnrollmentTokenInput input,
        IValidator<EnrollmentTokenInput> validator,
        PanelCommands commands,
        IConfiguration configuration,
        HttpContext http,
        CancellationToken ct)
    {
        var invalid = await ValidateAsync(input, validator, ct);
        if (invalid is not null)
            return invalid;

        var (result, token) = await commands.CreateEnrollmentTokenAsync(input, ct);
        return result.Succeeded ? Results.Ok(TokenResponse(token!, configuration, http)) : ToResult(result);
    }

    /// <summary>"Ver comando" de un token activo (decisión 73).</summary>
    private static async Task<IResult> RevealTokenAsync(int id, PanelCommands commands, IConfiguration configuration, HttpContext http, CancellationToken ct)
    {
        var (result, token) = await commands.RevealEnrollmentTokenAsync(id, ct);
        return result.Succeeded ? Results.Ok(TokenResponse(token!, configuration, http)) : ToResult(result);
    }

    private static object TokenResponse(CreatedEnrollmentToken token, IConfiguration configuration, HttpContext http)
    {
        // URL con la que los agentes llegan al servidor (detrás de un proxy puede diferir de la de esta petición).
        var serverUrl = configuration["Instaladores:UrlServidor"] ?? $"{http.Request.Scheme}://{http.Request.Host}";
        return new
        {
            id = token.Id,
            token = token.Token,
            vence = token.ExpiresAt,
            urlServidor = serverUrl,
            comando = $"{InstallerFileName} /S /SERVER={serverUrl} /TOKEN={token.Token}",
        };
    }

    /// <summary>El instalador (.exe) lo genera la compilación del agente; el servidor solo lo entrega.</summary>
    private static IResult DownloadInstaller(IConfiguration configuration)
    {
        var path = configuration["Instaladores:Archivo"];
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Results.Problem(
                title: "El instalador del agente todavía no está publicado en el servidor.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["codigo"] = "instalador_no_disponible" });
        }
        return Results.File(path, "application/vnd.microsoft.portable-executable", InstallerFileName);
    }

    internal static async Task<IResult?> ValidateAsync<T>(T input, IValidator<T> validator, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(input, ct);
        return validation.IsValid ? null : Results.ValidationProblem(validation.ToDictionary());
    }

    internal static IResult ToResult(CommandResult result, bool created = false)
    {
        if (result.Succeeded)
            return created ? Results.Created($"{result.Id}", new { id = result.Id }) : Results.Ok(new { id = result.Id });

        return result.Error switch
        {
            CommandError.NotFound => Results.NotFound(),
            CommandError.Conflict => Results.Problem(title: result.Message, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem(title: result.Message, statusCode: StatusCodes.Status400BadRequest),
        };
    }
}

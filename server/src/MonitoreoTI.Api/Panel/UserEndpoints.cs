using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentValidation;
using MonitoreoTI.Api.Auth;
using MonitoreoTI.Application.Users;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Users;
using static MonitoreoTI.Api.Panel.PanelWriteEndpoints;

namespace MonitoreoTI.Api.Panel;

/// <summary>Usuarios (W-36, W-37), roles y permisos (W-38) y Mi perfil (W-41).</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // ---------------------------------------------------------------- usuarios de la empresa
        var users = api.MapGroup("/usuarios").RequireAuthorization(PermissionPolicies.UsersAdmin);
        users.MapGet("/", async (UserAdministration service, HttpRequest request, CancellationToken ct) =>
            ListPaging.Ok(await service.ListAsync(ct), request));
        users.MapPost("/", async (UserInput input, IValidator<UserInput> validator, UserAdministration service, CancellationToken ct) =>
        {
            if (await ValidateAsync(input, validator, ct) is { } invalid)
                return invalid;
            var (result, link) = await service.CreateAsync(input, ct);
            return result.Succeeded ? Results.Created($"{result.Id}", new { id = result.Id, enlace = link }) : ToResult(result);
        });
        users.MapPut("/{id:int}", async (int id, UserInput input, IValidator<UserInput> validator, UserAdministration service, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await service.UpdateAsync(id, input, ct)));
        users.MapPost("/{id:int}/activar", async (int id, UserAdministration service, CancellationToken ct) =>
            ToResult(await service.SetActiveAsync(id, true, ct)));
        users.MapPost("/{id:int}/desactivar", async (int id, UserAdministration service, CancellationToken ct) =>
            ToResult(await service.SetActiveAsync(id, false, ct)));
        users.MapPost("/{id:int}/enlace-contrasena", async (int id, UserAdministration service, CancellationToken ct) =>
        {
            var (result, link) = await service.PasswordLinkAsync(id, ct);
            return result.Succeeded ? Results.Ok(link) : ToResult(result);
        });
        users.MapPost("/{id:int}/reiniciar-2fa", async (int id, UserAdministration service, CancellationToken ct) =>
            ToResult(await service.ResetTwoFactorAsync(id, ct)));

        // ---------------------------------------------------------------- roles y permisos
        var roles = api.MapGroup("/roles").RequireAuthorization(PermissionPolicies.UsersAdmin);
        roles.MapGet("/", (RoleAdministration service, CancellationToken ct) => service.ListAsync(ct));
        roles.MapPost("/", async (RoleInput input, IValidator<RoleInput> validator, RoleAdministration service, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await service.CreateAsync(input, ct), created: true));
        roles.MapPut("/{id:int}", async (int id, RoleInput input, IValidator<RoleInput> validator, RoleAdministration service, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await service.UpdateAsync(id, input, ct)));
        roles.MapDelete("/{id:int}", async (int id, RoleAdministration service, CancellationToken ct) =>
            ToResult(await service.DeleteAsync(id, ct)));
        api.MapGet("/permisos", (RoleAdministration service, CancellationToken ct) => service.PermissionsAsync(ct))
            .RequireAuthorization(PermissionPolicies.UsersAdmin);

        // ---------------------------------------------------------------- mi perfil (cualquier sesión, también sin empresa)
        var me = api.MapGroup("/auth").RequireAuthorization();
        me.MapGet("/perfil", async (ClaimsPrincipal principal, UserProfile profile, CancellationToken ct) =>
            await profile.GetAsync(AuthEndpoints.UserId(principal), ct) is { } data ? Results.Ok(data) : Results.NotFound());
        me.MapPut("/perfil", async (ProfileInput input, IValidator<ProfileInput> validator, ClaimsPrincipal principal, UserProfile profile, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct) ?? ToResult(await profile.RenameAsync(AuthEndpoints.UserId(principal), input, ct)));
        // Limitado como el login: aquí también se prueba una contraseña.
        me.MapPost("/contrasena", async (ChangePasswordInput input, IValidator<ChangePasswordInput> validator, ClaimsPrincipal principal, UserProfile profile, CancellationToken ct) =>
            await ValidateAsync(input, validator, ct)
                ?? ToResult(await profile.ChangePasswordAsync(AuthEndpoints.UserId(principal), SessionId(principal), input, ct)))
            .RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
        me.MapGet("/sesiones", async (ClaimsPrincipal principal, UserProfile profile, HttpRequest request, CancellationToken ct) =>
            ListPaging.Ok(await profile.SessionsAsync(AuthEndpoints.UserId(principal), SessionId(principal), ct), request));
        me.MapDelete("/sesiones/{id:long}", async (long id, ClaimsPrincipal principal, UserProfile profile, CancellationToken ct) =>
            ToResult(await profile.CloseSessionAsync(AuthEndpoints.UserId(principal), id, ct)));
        me.MapPost("/sesiones/cerrar-otras", async (ClaimsPrincipal principal, UserProfile profile, CancellationToken ct) =>
            Results.Ok(new { cerradas = await profile.CloseOtherSessionsAsync(AuthEndpoints.UserId(principal), SessionId(principal), ct) }));
    }

    private static long? SessionId(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(AuthClaims.SessionId), out var id) ? id : null;
}

/// <summary>
/// Nombre legible del cliente a partir del User-Agent. El panel agrega "Equipo/&lt;nombre del PC&gt;"; de otros
/// clientes solo se distingue el tipo. Nunca se guarda el User-Agent completo.
/// </summary>
public sealed partial class ClientDevice(IHttpContextAccessor accessor) : IClientDevice
{
    public string? Name
    {
        get
        {
            var agent = accessor.HttpContext?.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(agent))
                return null;

            var system = agent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
                : agent.Contains("Android", StringComparison.Ordinal) ? "Android"
                : agent.Contains("iPhone", StringComparison.Ordinal) || agent.Contains("iPad", StringComparison.Ordinal) ? "iOS"
                : agent.Contains("Mac OS", StringComparison.Ordinal) ? "macOS"
                : agent.Contains("Linux", StringComparison.Ordinal) ? "Linux" : null;

            var name = EquipoPattern().Match(agent) is { Success: true } match
                ? $"Centinela en {Uri.UnescapeDataString(match.Groups[1].Value)}"
                : agent.Contains("Electron/", StringComparison.Ordinal) ? "Centinela"
                : agent.Contains("Mozilla/", StringComparison.Ordinal) ? "Navegador" : "Otro programa";
            if (system is not null)
                name += $" ({system})";

            var clean = new string(name.Where(c => !char.IsControl(c)).ToArray());
            return clean.Length > 100 ? clean[..100] : clean;
        }
    }

    [GeneratedRegex(@"\bEquipo/([A-Za-z0-9%._-]{1,60})")]
    private static partial Regex EquipoPattern();
}

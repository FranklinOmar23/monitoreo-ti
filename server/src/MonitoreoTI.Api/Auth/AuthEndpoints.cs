using System.Security.Claims;
using FluentValidation;
using Microsoft.IdentityModel.JsonWebTokens;
using MonitoreoTI.Application.Auth;
using MonitoreoTI.Infrastructure.Auth;

namespace MonitoreoTI.Api.Auth;

public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("auth");

        // Sin sesión todavía: limitados por IP contra fuerza bruta (además del bloqueo por usuario).
        var anonymous = auth.MapGroup("").RequireRateLimiting(RateLimitPolicy);
        anonymous.MapPost("/login", LoginAsync);
        anonymous.MapPost("/verificar-2fa", VerifyTwoFactorAsync);
        anonymous.MapPost("/activar-2fa", ConfirmTwoFactorSetupAsync);
        anonymous.MapPost("/seleccionar-empresa", SelectCompanyAsync);
        anonymous.MapPost("/refresh", RefreshAsync);
        anonymous.MapPost("/logout", LogoutAsync);
        anonymous.MapPost("/recuperar", RequestResetAsync);
        anonymous.MapPost("/restablecer", ResetAsync);

        auth.MapGet("/sesion", GetSessionAsync).RequireAuthorization();
        auth.MapPost("/cambiar-empresa", SwitchCompanyAsync).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request, IValidator<LoginRequest> validator, IAuthService auth, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        return ToResult(await auth.LoginAsync(request.Correo, request.Contrasena, Ip(http), ct));
    }

    private static async Task<IResult> VerifyTwoFactorAsync(
        ChallengeCodeRequest request, IValidator<ChallengeCodeRequest> validator, IAuthService auth, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        return ToResult(await auth.VerifyTwoFactorAsync(request.Desafio, request.Codigo, Ip(http), ct));
    }

    private static async Task<IResult> ConfirmTwoFactorSetupAsync(
        ChallengeCodeRequest request, IValidator<ChallengeCodeRequest> validator, IAuthService auth, HttpContext http, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        return ToResult(await auth.ConfirmTwoFactorSetupAsync(request.Desafio, request.Codigo, Ip(http), ct));
    }

    private static async Task<IResult> SelectCompanyAsync(SelectCompanyRequest request, IAuthService auth, HttpContext http, CancellationToken ct) =>
        ToResult(await auth.SelectCompanyAsync(request.Desafio ?? string.Empty, request.EmpresaId, Ip(http), ct));

    private static async Task<IResult> RefreshAsync(RefreshRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 200)
            return Error(AuthError.InvalidRefreshToken);

        var outcome = await auth.RefreshAsync(request.RefreshToken, Ip(http), ct);
        return outcome.Succeeded ? Results.Ok(SessionDto(outcome.Value!)) : Error(outcome.Error!.Value);
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, IAuthService auth, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken) && request.RefreshToken.Length <= 200)
            await auth.LogoutAsync(request.RefreshToken, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RequestResetAsync(PasswordResetRequest request, IAuthService auth, HttpContext http, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.Correo) && request.Correo.Length <= 200)
            await auth.RequestPasswordResetAsync(request.Correo, Ip(http), ct);

        // Igual respuesta exista o no el correo.
        return Results.Accepted();
    }

    private static async Task<IResult> ResetAsync(
        PasswordResetConfirmRequest request, IValidator<PasswordResetConfirmRequest> validator, IAuthService auth, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return Results.ValidationProblem(validation.ToDictionary());

        var outcome = await auth.ResetPasswordAsync(request.Token, request.NuevaContrasena, ct);
        return outcome.Succeeded ? Results.NoContent() : Error(outcome.Error!.Value);
    }

    private static async Task<IResult> GetSessionAsync(ClaimsPrincipal user, IAuthService auth, CancellationToken ct)
    {
        var session = await auth.GetSessionAsync(UserId(user), CompanyId(user), ct);
        if (session is null)
            return Results.Unauthorized();

        return Results.Ok(new
        {
            usuario = new { id = session.UserId, nombre = session.Name, correo = session.Email, esSuperadmin = session.IsSuperAdmin },
            empresa = session.CompanyId is null ? null : new { id = session.CompanyId, nombre = session.CompanyName, rol = session.Role },
            permisos = session.Permissions,
            empresas = session.Companies.Select(CompanyDto),
        });
    }

    private static async Task<IResult> SwitchCompanyAsync(
        SwitchCompanyRequest request, ClaimsPrincipal user, IAuthService auth, HttpContext http, CancellationToken ct)
    {
        var outcome = await auth.SwitchCompanyAsync(UserId(user), request.EmpresaId, Ip(http), ct);
        return outcome.Succeeded ? Results.Ok(SessionDto(outcome.Value!)) : Error(outcome.Error!.Value);
    }

    private static IResult ToResult(AuthOutcome<LoginResult> outcome)
    {
        if (!outcome.Succeeded)
            return Error(outcome.Error!.Value);

        var result = outcome.Value!;
        return Results.Ok(new
        {
            paso = result.Step switch
            {
                LoginStep.Completed => "completado",
                LoginStep.TwoFactorRequired => "verificar-2fa",
                LoginStep.TwoFactorSetupRequired => "activar-2fa",
                LoginStep.CompanySelectionRequired => "elegir-empresa",
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            },
            desafio = result.Challenge,
            sesion = result.Session is null ? null : SessionDto(result.Session),
            empresas = result.Companies?.Select(CompanyDto),
            activacion2fa = result.TwoFactorSetup is null ? null : new
            {
                secreto = result.TwoFactorSetup.Secret,
                uri = result.TwoFactorSetup.OtpAuthUri,
                qr = result.TwoFactorSetup.QrPngDataUri,
            },
        });
    }

    private static object SessionDto(SessionTokens s) => new
    {
        accessToken = s.AccessToken,
        accessTokenExpira = s.AccessTokenExpiresAt,
        refreshToken = s.RefreshToken,
        refreshTokenExpira = s.RefreshTokenExpiresAt,
    };

    private static object CompanyDto(CompanyOption c) => new { id = c.Id, nombre = c.Name, rol = c.Role };

    /// <summary>Errores con un código estable para que el cliente muestre el mensaje correcto.</summary>
    private static IResult Error(AuthError error)
    {
        var (status, code, title) = error switch
        {
            AuthError.InvalidCredentials => (401, "credenciales_invalidas", "Correo o contraseña incorrectos."),
            AuthError.LockedOut => (423, "cuenta_bloqueada", "Demasiados intentos. Espere unos minutos e intente de nuevo."),
            AuthError.InvalidCode => (401, "codigo_invalido", "El código no es válido. Revise la hora del teléfono e intente con el siguiente."),
            AuthError.InvalidChallenge => (401, "desafio_invalido", "La verificación venció. Inicie sesión de nuevo."),
            AuthError.InvalidRefreshToken => (401, "sesion_vencida", "La sesión venció. Inicie sesión de nuevo."),
            AuthError.CompanyNotAllowed => (403, "empresa_no_permitida", "No tiene acceso a esa empresa."),
            AuthError.InvalidResetToken => (400, "enlace_invalido", "El enlace no es válido o ya venció. Pida uno nuevo."),
            AuthError.WeakPassword => (400, "contrasena_debil", $"La contraseña debe tener al menos {PasswordRules.MinLength} caracteres, con letras y números."),
            _ => (400, "error", "No se pudo completar la operación."),
        };
        return Results.Problem(title: title, statusCode: status, extensions: new Dictionary<string, object?> { ["codigo"] = code });
    }

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    public static int UserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

    public static int? CompanyId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(AuthClaims.CompanyId), out var id) ? id : null;
}

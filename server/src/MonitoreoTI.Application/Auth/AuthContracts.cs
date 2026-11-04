using FluentValidation;

namespace MonitoreoTI.Application.Auth;

/// <summary>Claves de la tabla permisos que usa el código. Los roles solo agrupan permisos.</summary>
public static class PermissionKeys
{
    public const string DashboardView = "dashboard.ver";
    public const string MachinesView = "equipos.ver";
    public const string MachinesEdit = "equipos.editar";
    public const string BranchesEdit = "sucursales.editar";
    public const string InstallersGenerate = "instaladores.generar";
    public const string AlertsView = "alertas.ver";
    public const string AlertsManage = "alertas.gestionar";
    public const string TicketsCreate = "tickets.crear";
    public const string TicketsManage = "tickets.gestionar";
    public const string NetworkView = "red.ver";
    public const string NetworkEdit = "red.editar";
    public const string UsersAdmin = "usuarios.administrar";
    public const string AuditView = "auditoria.ver";
    public const string ReportsView = "reportes.ver";
    public const string SettingsEdit = "configuracion.editar";
    public const string RemoteConnect = "remoto.conectar";
    public const string RemoteTerminal = "remoto.terminal";
    public const string RemoteFiles = "remoto.archivos";
    public const string ScriptsRun = "scripts.ejecutar";
    public const string ScriptsEdit = "scripts.editar";

    /// <summary>Permisos que dan control de equipos ajenos: exigen 2FA (sección 7.1).</summary>
    public static readonly IReadOnlySet<string> RequiringTwoFactor = new HashSet<string>(StringComparer.Ordinal)
    {
        "remoto.conectar", "remoto.terminal", "remoto.archivos", "scripts.ejecutar",
    };
}

/// <summary>
/// Paso siguiente del inicio de sesión. Cada paso intermedio entrega un "desafío": un token firmado
/// de pocos minutos que solo sirve para completar ese paso, nunca para usar la API.
/// </summary>
public enum LoginStep
{
    /// <summary>Sesión emitida.</summary>
    Completed,
    /// <summary>Pedir el código de la app autenticadora.</summary>
    TwoFactorRequired,
    /// <summary>El rol exige 2FA y el usuario aún no lo activó: mostrar el QR y pedir el primer código.</summary>
    TwoFactorSetupRequired,
    /// <summary>El usuario pertenece a varias empresas: elegir una.</summary>
    CompanySelectionRequired,
}

public sealed record CompanyOption(int Id, string Name, string Role);

public sealed record TwoFactorSetup(string Secret, string OtpAuthUri, string QrPngDataUri);

public sealed record SessionTokens(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt);

public sealed record LoginResult(
    LoginStep Step,
    string? Challenge = null,
    SessionTokens? Session = null,
    IReadOnlyList<CompanyOption>? Companies = null,
    TwoFactorSetup? TwoFactorSetup = null);

/// <summary>Resultado de una operación de autenticación: éxito con datos, o un error con código estable.</summary>
public sealed record AuthOutcome<T>(T? Value, AuthError? Error)
{
    public bool Succeeded => Error is null;
    public static AuthOutcome<T> Ok(T value) => new(value, null);
    public static AuthOutcome<T> Fail(AuthError error) => new(default, error);
}

public enum AuthError
{
    /// <summary>Correo o contraseña incorrectos, o usuario inactivo (no se distingue).</summary>
    InvalidCredentials,
    /// <summary>Demasiados intentos fallidos: cuenta bloqueada unos minutos.</summary>
    LockedOut,
    InvalidCode,
    /// <summary>Desafío vencido, alterado o de otro paso.</summary>
    InvalidChallenge,
    InvalidRefreshToken,
    /// <summary>El usuario no pertenece (o ya no) a la empresa pedida.</summary>
    CompanyNotAllowed,
    InvalidResetToken,
    WeakPassword,
}

public sealed record CurrentSession(
    int UserId,
    string Name,
    string Email,
    int? CompanyId,
    string? CompanyName,
    string? Role,
    bool IsSuperAdmin,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<CompanyOption> Companies);

public interface IAuthService
{
    Task<AuthOutcome<LoginResult>> LoginAsync(string email, string password, string? ip, CancellationToken cancellationToken);
    Task<AuthOutcome<LoginResult>> VerifyTwoFactorAsync(string challenge, string code, string? ip, CancellationToken cancellationToken);
    Task<AuthOutcome<LoginResult>> ConfirmTwoFactorSetupAsync(string challenge, string code, string? ip, CancellationToken cancellationToken);

    /// <summary>Código 2FA con la sesión abierta, para el acceso remoto; devuelve la hora del 2FA.</summary>
    Task<AuthOutcome<DateTime>> ConfirmRecentTwoFactorAsync(int userId, string code, string? ip, CancellationToken cancellationToken);
    Task<AuthOutcome<LoginResult>> SelectCompanyAsync(string challenge, int companyId, string? ip, CancellationToken cancellationToken);
    Task<AuthOutcome<SessionTokens>> RefreshAsync(string refreshToken, string? ip, CancellationToken cancellationToken);
    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken);
    Task<AuthOutcome<SessionTokens>> SwitchCompanyAsync(int userId, int companyId, string? ip, CancellationToken cancellationToken);
    Task<CurrentSession?> GetSessionAsync(int userId, int? companyId, CancellationToken cancellationToken);

    /// <summary>Siempre "acepta" (no revela si el correo existe); si existe, envía el enlace.</summary>
    Task RequestPasswordResetAsync(string email, string? ip, CancellationToken cancellationToken);
    Task<AuthOutcome<bool>> ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken);
}

public interface IEmailSender
{
    /// <summary>
    /// El correo llega de verdad al buzón. Si no (archivo de desarrollo, sin SMTP), el panel muestra los enlaces
    /// de contraseña para entregarlos a mano.
    /// </summary>
    bool Delivers => true;

    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}

public static class PasswordRules
{
    public const int MinLength = 10;

    /// <summary>Mínimo 10 caracteres con letras y números (sección 15).</summary>
    public static bool IsStrong(string? password) =>
        password is { Length: >= MinLength and <= 128 }
        && password.Any(char.IsLetter)
        && password.Any(char.IsDigit);
}

public sealed record LoginRequest(string Correo, string Contrasena);
public sealed record ChallengeCodeRequest(string Desafio, string Codigo);
public sealed record SelectCompanyRequest(string Desafio, int EmpresaId);
public sealed record RefreshRequest(string RefreshToken);
public sealed record SwitchCompanyRequest(int EmpresaId);
public sealed record PasswordResetRequest(string Correo);
public sealed record PasswordResetConfirmRequest(string Token, string NuevaContrasena);

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Correo).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.Contrasena).NotEmpty().MaximumLength(128);
    }
}

public sealed class ChallengeCodeRequestValidator : AbstractValidator<ChallengeCodeRequest>
{
    public ChallengeCodeRequestValidator()
    {
        RuleFor(x => x.Desafio).NotEmpty().MaximumLength(4096);
        RuleFor(x => x.Codigo).NotEmpty().Matches("^[0-9]{6}$").WithMessage("El código tiene 6 dígitos.");
    }
}

public sealed class PasswordResetConfirmRequestValidator : AbstractValidator<PasswordResetConfirmRequest>
{
    public PasswordResetConfirmRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.NuevaContrasena).Must(PasswordRules.IsStrong)
            .WithMessage($"La contraseña debe tener al menos {PasswordRules.MinLength} caracteres, con letras y números.");
    }
}

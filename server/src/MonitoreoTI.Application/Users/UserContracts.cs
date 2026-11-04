using FluentValidation;
using MonitoreoTI.Application.Auth;

namespace MonitoreoTI.Application.Users;

// ------------------------------------------------------------------ usuarios (W-36, W-37)

/// <summary>Alta o edición de un usuario de la empresa. Sin sucursales = ve todas (decisión 28).</summary>
public sealed record UserInput(string Nombre, string Correo, int RolId, IReadOnlyList<int>? SucursalIds);

public sealed class UserInputValidator : AbstractValidator<UserInput>
{
    public UserInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre.").MaximumLength(150);
        RuleFor(x => x.Correo).NotEmpty().WithMessage("Escriba el correo.").MaximumLength(200)
            .EmailAddress().WithMessage("El correo no es válido.");
        RuleFor(x => x.RolId).GreaterThan(0).WithMessage("Elija el rol.");
        RuleFor(x => x.SucursalIds).Must(ids => ids is null || ids.Count <= 200).WithMessage("Demasiadas sucursales.");
    }
}

public sealed record BranchRef(int Id, string Name);

public sealed record UserItem(
    int Id,
    string Name,
    string Email,
    int RoleId,
    string RoleName,
    IReadOnlyList<BranchRef> Branches,
    bool IsActive,
    bool TwoFactorEnabled,
    /// <summary>Su rol da acceso remoto: debe activar el 2FA al iniciar sesión.</summary>
    bool TwoFactorRequired,
    DateTime? LastLoginAt,
    DateTime CreatedAt,
    /// <summary>Nunca inició sesión (todavía no creó su contraseña).</summary>
    bool Pending,
    bool LockedOut,
    /// <summary>También pertenece a otra empresa: su nombre, correo, contraseña y 2FA solo los cambia él.</summary>
    bool Shared,
    bool IsSelf);

/// <summary>
/// Enlace para crear o restablecer la contraseña. Link solo llega al panel si el servidor no envía correos
/// de verdad (para entregarlo a mano); con correo configurado va únicamente al buzón del usuario.
/// </summary>
public sealed record PasswordLink(int UserId, bool Emailed, string? Link, DateTime ExpiresAt);

// ------------------------------------------------------------------ roles y permisos (W-38)

public sealed record RoleInput(string Nombre, string? Descripcion, IReadOnlyList<string> Permisos);

public sealed class RoleInputValidator : AbstractValidator<RoleInput>
{
    public RoleInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba el nombre del rol.").MaximumLength(50);
        RuleFor(x => x.Descripcion).MaximumLength(200);
        RuleFor(x => x.Permisos).NotNull().Must(p => p.Count <= 100).WithMessage("Demasiados permisos.");
    }
}

public sealed record RoleItem(
    int Id,
    string Name,
    string? Description,
    /// <summary>Rol del sistema: se ve y se duplica, no se cambia.</summary>
    bool IsSystem,
    IReadOnlyList<string> Permissions,
    int UserCount);

public sealed record PermissionItem(string Key, string Description, string Group, bool RequiresTwoFactor);

/// <summary>Agrupa los permisos por módulo para la matriz de W-38.</summary>
public static class PermissionGroups
{
    public static readonly IReadOnlyList<string> Order = ["General", "Equipos", "Alertas y tickets", "Acceso remoto", "Red", "Administración"];

    public static string Of(string key) => key.Split('.')[0] switch
    {
        "dashboard" or "reportes" => "General",
        "equipos" or "sucursales" or "instaladores" => "Equipos",
        "alertas" or "tickets" => "Alertas y tickets",
        "remoto" or "scripts" => "Acceso remoto",
        "red" => "Red",
        _ => "Administración",
    };
}

// ------------------------------------------------------------------ mi perfil (W-41)

public sealed record ProfileInput(string Nombre);

public sealed class ProfileInputValidator : AbstractValidator<ProfileInput>
{
    public ProfileInputValidator()
    {
        RuleFor(x => x.Nombre).NotEmpty().WithMessage("Escriba su nombre.").MaximumLength(150);
    }
}

public sealed record ChangePasswordInput(string ContrasenaActual, string NuevaContrasena);

public sealed class ChangePasswordInputValidator : AbstractValidator<ChangePasswordInput>
{
    public ChangePasswordInputValidator()
    {
        RuleFor(x => x.ContrasenaActual).NotEmpty().WithMessage("Escriba su contraseña actual.");
        RuleFor(x => x.NuevaContrasena).Must(PasswordRules.IsStrong)
            .WithMessage($"La contraseña debe tener al menos {PasswordRules.MinLength} caracteres, con letras y números.");
        RuleFor(x => x.NuevaContrasena).NotEqual(x => x.ContrasenaActual).WithMessage("La contraseña nueva debe ser distinta de la actual.");
    }
}

public sealed record ProfileData(
    int Id,
    string Name,
    string Email,
    bool TwoFactorEnabled,
    DateTime? LastTwoFactorAt,
    bool NotifyByEmail,
    DateTime CreatedAt);

public sealed record OpenSessionItem(long Id, DateTime StartedAt, DateTime ExpiresAt, string? Ip, string? Device, bool Current);

/// <summary>Nombre del equipo o programa que hace la petición (lo pone el API a partir del User-Agent).</summary>
public interface IClientDevice
{
    string? Name { get; }
}

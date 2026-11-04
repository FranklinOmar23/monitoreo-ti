namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>
/// Configuración de autenticación (sección "Auth" de la configuración). Las llaves nunca van en el repositorio:
/// user-secrets en desarrollo, variables de entorno en producción.
/// </summary>
public sealed class AuthOptions
{
    public string Issuer { get; set; } = "monitoreoti";
    public string Audience { get; set; } = "monitoreoti-clientes";

    /// <summary>Llave HMAC-SHA256 para firmar los JWT, en Base64 (mínimo 32 bytes).</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Llave AES-256 (Base64, 32 bytes) que cifra los secretos 2FA guardados en la base.</summary>
    public string EncryptionKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Vida de los pasos intermedios del login (2FA, elegir empresa).</summary>
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int LockoutThreshold { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Enlace que crea un administrador (usuario nuevo o restablecer): da tiempo a entregarlo.</summary>
    public TimeSpan PasswordInvitationLifetime { get; set; } = TimeSpan.FromHours(72);

    /// <summary>Enlace del correo de recuperación; el token se agrega al final.</summary>
    public string PasswordResetUrl { get; set; } = "monitoreoti://restablecer?token=";

    /// <summary>Nombre que muestra la app autenticadora.</summary>
    public string TotpIssuer { get; set; } = "MonitoreoTI";

    public byte[] SigningKeyBytes() => DecodeKey(SigningKey, nameof(SigningKey), minLength: 32);
    public byte[] EncryptionKeyBytes() => DecodeKey(EncryptionKey, nameof(EncryptionKey), minLength: 32, exactLength: 32);

    /// <summary>Falla al arrancar, con un mensaje claro, si faltan las llaves.</summary>
    public void Validate()
    {
        SigningKeyBytes();
        EncryptionKeyBytes();
    }

    private static byte[] DecodeKey(string value, string name, int minLength, int? exactLength = null)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            bytes = [];
        }

        if (bytes.Length < minLength || (exactLength is int exact && bytes.Length != exact))
        {
            throw new InvalidOperationException(
                $"Falta o es inválida la llave 'Auth:{name}' (Base64, {exactLength ?? minLength} bytes). " +
                "En desarrollo: dotnet user-secrets set \"Auth:" + name + "\" \"<llave en Base64>\".");
        }
        return bytes;
    }
}

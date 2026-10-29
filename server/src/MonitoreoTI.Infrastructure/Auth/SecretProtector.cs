using System.Security.Cryptography;
using System.Text;

namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>
/// Cifra secretos que deben guardarse recuperables (secreto 2FA) con AES-256-GCM.
/// La llave vive fuera de la base (sección 15); el prefijo de versión permite rotarla más adelante.
/// Formato: "v1:" + Base64(nonce[12] | texto cifrado | etiqueta[16]).
/// </summary>
public sealed class SecretProtector(AuthOptions options)
{
    private const string Version = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key = options.EncryptionKeyBytes();

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize));
        return Version + Convert.ToBase64String(output);
    }

    /// <summary>null si el valor fue alterado o se cifró con otra llave.</summary>
    public string? Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Version, StringComparison.Ordinal))
            return null;

        try
        {
            var data = Convert.FromBase64String(protectedValue[Version.Length..]);
            if (data.Length < NonceSize + TagSize)
                return null;

            var cipherLength = data.Length - NonceSize - TagSize;
            var plain = new byte[cipherLength];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize, cipherLength), data.AsSpan(NonceSize + cipherLength), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

using System.Security.Cryptography;
using System.Text;

namespace MonitoreoTI.Application.Agents;

/// <summary>
/// Tokens de registro y credenciales permanentes de agentes. Formato: "{empresaId}.{secreto}".
/// El prefijo con la empresa permite fijar el contexto de Row-Level Security antes de buscar el token
/// (se llega aquí sin sesión). No es un secreto: lo que autentica es el secreto aleatorio de 256 bits,
/// y en la base solo se guarda el SHA-256 del token completo (hex en minúsculas).
/// </summary>
public static class AgentTokens
{
    private const int SecretBytes = 32;
    public const int MaxLength = 128;

    public static (string Token, string Hash) Generate(int companyId)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"{companyId}.{secret}";
        return (token, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Extrae la empresa del prefijo; false si el token no tiene el formato esperado.</summary>
    public static bool TryGetCompanyId(string? token, out int companyId)
    {
        companyId = 0;
        if (string.IsNullOrEmpty(token) || token.Length > MaxLength)
            return false;

        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            return false;

        return int.TryParse(token.AsSpan(0, dot), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out companyId)
               && companyId > 0;
    }
}

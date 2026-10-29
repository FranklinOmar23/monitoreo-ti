using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MonitoreoTI.Infrastructure.Auth;

/// <summary>Nombres de los claims propios del sistema.</summary>
public static class AuthClaims
{
    public const string CompanyId = "empresa_id";
    public const string Role = "rol";
    public const string Permission = "perm";
    public const string SuperAdmin = "sa";

    // Solo en desafíos.
    public const string Purpose = "paso";
    public const string PendingSecret = "pend";
}

/// <summary>Pasos del login que se encadenan con desafíos.</summary>
public static class ChallengePurpose
{
    public const string TwoFactor = "2fa";
    public const string TwoFactorSetup = "2fa-alta";
    public const string Company = "empresa";
}

public sealed record AccessTokenContent(
    int UserId, string Name, string Email, int? CompanyId, string? Role, bool IsSuperAdmin, IReadOnlyCollection<string> Permissions);

/// <summary>
/// Emite y valida los JWT. Los desafíos usan otra audiencia: un desafío nunca sirve como token de acceso
/// (y viceversa), aunque ambos los firme la misma llave.
/// </summary>
public sealed class AuthTokenService(AuthOptions options, TimeProvider clock)
{
    public const string ChallengeAudienceSuffix = "-desafio";

    private readonly SymmetricSecurityKey _key = new(options.SigningKeyBytes());
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    private string ChallengeAudience => options.Audience + ChallengeAudienceSuffix;

    public TokenValidationParameters AccessValidationParameters() => new()
    {
        ValidIssuer = options.Issuer,
        ValidAudience = options.Audience,
        IssuerSigningKey = _key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = AuthClaims.Role,
    };

    public (string Token, DateTime ExpiresAt) CreateAccessToken(AccessTokenContent content)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, content.UserId.ToString()),
            new(JwtRegisteredClaimNames.Name, content.Name),
            new(JwtRegisteredClaimNames.Email, content.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (content.CompanyId is int companyId)
            claims.Add(new Claim(AuthClaims.CompanyId, companyId.ToString()));
        if (content.Role is not null)
            claims.Add(new Claim(AuthClaims.Role, content.Role));
        if (content.IsSuperAdmin)
            claims.Add(new Claim(AuthClaims.SuperAdmin, "1"));
        claims.AddRange(content.Permissions.Select(p => new Claim(AuthClaims.Permission, p)));

        var expires = clock.GetUtcNow().UtcDateTime + options.AccessTokenLifetime;
        return (Create(claims, options.Audience, expires), expires);
    }

    public string CreateChallenge(int userId, string purpose, string? pendingSecret = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(AuthClaims.Purpose, purpose),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (pendingSecret is not null)
            claims.Add(new Claim(AuthClaims.PendingSecret, pendingSecret));

        return Create(claims, ChallengeAudience, clock.GetUtcNow().UtcDateTime + options.ChallengeLifetime);
    }

    /// <summary>Valida firma, vigencia, audiencia y paso. Devuelve el usuario y, si lo hay, el secreto pendiente (cifrado).</summary>
    public async Task<(int UserId, string? PendingSecret)?> ValidateChallengeAsync(string challenge, string expectedPurpose)
    {
        var result = await _handler.ValidateTokenAsync(challenge, new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = ChallengeAudience,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(10),
            LifetimeValidator = (_, expires, _, _) => expires > clock.GetUtcNow().UtcDateTime,
        });

        if (!result.IsValid
            || !result.Claims.TryGetValue(AuthClaims.Purpose, out var purpose) || purpose as string != expectedPurpose
            || !result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub) || !int.TryParse(sub as string, out var userId))
            return null;

        result.Claims.TryGetValue(AuthClaims.PendingSecret, out var pending);
        return (userId, pending as string);
    }

    /// <summary>Token opaco aleatorio (refresh o restablecimiento) y su SHA-256, que es lo único que se guarda.</summary>
    public static (string Token, string Hash) NewOpaqueToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (token, HashOpaqueToken(token));
    }

    public static string HashOpaqueToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private string Create(IEnumerable<Claim> claims, string audience, DateTime expires)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
    }
}

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Remote;
using MonitoreoTI.Domain.Entities.Companies;
using MonitoreoTI.Infrastructure.Auth;
using MonitoreoTI.Infrastructure.Persistence;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>
/// Llave ECDSA P-256 de la empresa del contexto (sección 5): la privada se guarda cifrada con AES-GCM (la llave de
/// cifrado vive fuera de la base) y solo se abre en memoria para firmar. Se crea la primera vez que hace falta.
/// </summary>
public sealed class CompanySigner(MonitoreoDbContext db, SecretProtector protector) : ICompanySigner
{
    private CompanyKey? _key;

    public async Task<string> PublicKeyAsync(CancellationToken cancellationToken) => (await KeyAsync(cancellationToken)).PublicKey;

    public async Task<string> SignAsync(string payload, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(cancellationToken);
        var pkcs8 = protector.Unprotect(Encoding.UTF8.GetString(key.EncryptedPrivateKey))
            ?? throw new InvalidOperationException("No se pudo abrir la llave de firma de la empresa (¿cambió Auth:EncryptionKey?).");
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pkcs8), out _);
        return Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public async Task<bool> VerifyAsync(string payload, string signature, CancellationToken cancellationToken)
    {
        var key = await KeyAsync(cancellationToken);
        try
        {
            return Verify(key.PublicKey, payload, signature);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Verificación con la llave pública (la misma que hace el agente).</summary>
    public static bool Verify(string publicKey, string payload, string signature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private async Task<CompanyKey> KeyAsync(CancellationToken cancellationToken)
    {
        if (_key is not null)
            return _key;

        _key = await db.CompanyKeys.AsNoTracking().Where(k => k.IsActive).OrderByDescending(k => k.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (_key is not null)
            return _key;

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var created = new CompanyKey
        {
            PublicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            EncryptedPrivateKey = Encoding.UTF8.GetBytes(protector.Protect(Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()))),
        };
        db.CompanyKeys.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            _key = created;
        }
        catch (DbUpdateException)
        {
            // Otra petición la creó al mismo tiempo (índice único de la llave activa): se usa esa.
            db.Entry(created).State = EntityState.Detached;
            _key = await db.CompanyKeys.AsNoTracking().Where(k => k.IsActive).OrderByDescending(k => k.CreatedAt).FirstAsync(cancellationToken);
        }

        return _key;
    }
}

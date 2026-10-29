using MonitoreoTI.Domain.Common;

namespace MonitoreoTI.Domain.Entities.Companies;

/// <summary>Par de llaves ECDSA P-256 para firmar comandos y scripts. Tabla: empresa_llaves.</summary>
public class CompanyKey : ITenantEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>SubjectPublicKeyInfo en Base64.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>Llave privada cifrada por la aplicación (AES-GCM).</summary>
    public byte[] EncryptedPrivateKey { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

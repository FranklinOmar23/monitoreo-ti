using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MonitoreoTI.Application.Remote;

namespace MonitoreoTI.Infrastructure.Remote;

/// <summary>
/// La versión del agente publicada: el instalador de Instaladores:Archivo (su versión va incrustada en el .exe), su
/// SHA-256 y la firma con la llave del producto (Actualizaciones:LlavePrivada, PKCS#8 en Base64, fuera del
/// repositorio). Se recalcula solo si el archivo cambia.
/// </summary>
public sealed class AgentReleases(IConfiguration configuration, ILogger<AgentReleases> logger)
{
    private readonly object _gate = new();
    private (string Path, DateTime Modified, long Size, AgentRelease Release)? _cached;

    public string? InstallerPath => configuration["Instaladores:Archivo"];

    /// <summary>null con el motivo si no hay nada que ofrecer.</summary>
    public (AgentRelease? Release, string? Problem) Current()
    {
        var path = InstallerPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return (null, "El instalador del agente no está publicado en el servidor (Instaladores:Archivo).");
        var key = configuration["Actualizaciones:LlavePrivada"];
        if (string.IsNullOrWhiteSpace(key))
            return (null, "Falta la llave de firma de las actualizaciones (Actualizaciones:LlavePrivada).");

        var info = new FileInfo(path);
        lock (_gate)
        {
            if (_cached is { } c && c.Path == path && c.Modified == info.LastWriteTimeUtc && c.Size == info.Length)
                return (c.Release, null);
        }

        var version = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim();
        if (string.IsNullOrWhiteSpace(version))
            return (null, "No se pudo leer la versión del instalador.");

        string sha256;
        using (var stream = File.OpenRead(path))
            sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        string signature;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key), out _);
            signature = Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(UpdateSigning.Payload(version, sha256)),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            logger.LogError("La llave de firma de las actualizaciones no es válida.");
            return (null, "La llave de firma de las actualizaciones no es válida.");
        }

        var release = new AgentRelease(version, sha256, info.Length, signature);
        lock (_gate)
            _cached = (path, info.LastWriteTimeUtc, info.Length, release);
        logger.LogInformation("Versión del agente publicada: {Version} ({Sha256}).", version, sha256);
        return (release, null);
    }
}

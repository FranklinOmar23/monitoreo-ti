using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MonitoreoTI.AgentHost;

/// <summary>
/// Instala la versión que descargó el agente (sección 8.1, decisión 53). El agente termina con <see cref="ExitCode"/> y
/// deja update-pending.json en la carpeta de datos; aquí se vuelve a verificar todo (archivo dentro de "updates",
/// SHA-256 y firma de la llave del producto) antes de ejecutar el instalador en silencio. El instalador detiene este
/// servicio, reemplaza los archivos y lo vuelve a crear con la configuración y la credencial existentes.
/// </summary>
public static class UpdateApplier
{
    public const int ExitCode = 75;

    /// <summary>Llave pública del producto (la misma que tiene el agente en updater.ts).</summary>
    public const string ProductPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvv7LSrnvcjzHw7jVrgZnrWnKQ5Ang7yAlQltWZherSm1atJSMIgU8bTA2U2wD3uYg489JoNM1brmc0nYSQZyQA==";

    private sealed record Pending(string Version, string File, string Sha256, string Firma);

    public static string PendingFile(AgentPaths paths) => Path.Combine(paths.DataDirectory, "update-pending.json");

    /// <summary>Verifica y lanza el instalador. Devuelve el proceso lanzado, o null (con el motivo en la bitácora).</summary>
    public static Process? TryStart(AgentPaths paths, Action<string> log, string publicKey = ProductPublicKey)
    {
        var pendingPath = PendingFile(paths);
        try
        {
            var pending = Verify(paths, pendingPath, publicKey, out var problem);
            if (pending is null)
            {
                log($"Actualización descartada: {problem}");
                return null;
            }

            log($"Instalando la versión {pending.Version}…");
            var start = new ProcessStartInfo(pending.File) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/S");
            return Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            log($"No se pudo instalar la actualización: {ex.Message}");
            return null;
        }
        finally
        {
            // Un pedido se intenta una sola vez: si falla, el servidor lo vuelve a ofrecer más tarde.
            File.Delete(pendingPath);
        }
    }

    private static Pending? Verify(AgentPaths paths, string pendingPath, string publicKey, out string problem)
    {
        problem = "";
        if (!File.Exists(pendingPath))
        {
            problem = "no hay un pedido de actualización.";
            return null;
        }
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(pendingPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (pending is null)
        {
            problem = "el pedido está vacío.";
            return null;
        }

        var updates = Path.GetFullPath(Path.Combine(paths.DataDirectory, "updates")) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(pending.File);
        if (!file.StartsWith(updates, StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
        {
            problem = "el instalador no está en la carpeta de actualizaciones.";
            return null;
        }

        string sha;
        using (var stream = File.OpenRead(file))
            sha = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(sha, pending.Sha256, StringComparison.Ordinal))
        {
            problem = "el SHA-256 del instalador no coincide.";
            return null;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        var payload = Encoding.UTF8.GetBytes($"monitoreoti/actualizacion/v1\n{pending.Version}\n{sha}");
        if (!ecdsa.VerifyData(payload, Convert.FromBase64String(pending.Firma), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            problem = "la firma no es de la llave del producto.";
            return null;
        }

        return pending with { File = file };
    }
}

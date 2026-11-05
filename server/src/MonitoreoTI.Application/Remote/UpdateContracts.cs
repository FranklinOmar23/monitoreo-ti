namespace MonitoreoTI.Application.Remote;

// ------------------------------------------------------------------ actualización automática del agente (6.6)

/// <summary>
/// Texto que firma el servidor con la llave del producto (decisión 53) y que verifican el agente y el anfitrión antes
/// de instalar (espejo de agent/src/service/updater.ts y del anfitrión C#).
/// </summary>
public static class UpdateSigning
{
    public const string Prefix = "monitoreoti/actualizacion/v1";

    public static string Payload(string version, string sha256) => string.Join('\n', Prefix, version, sha256);
}

/// <summary>La versión del agente que el servidor ofrece: el instalador publicado, con su hash y su firma.</summary>
public sealed record AgentRelease(string Version, string Sha256, long Size, string Signature);

public static class VersionRules
{
    /// <summary>Compara "1.2.10" con "1.2.9" por números (lo que no es número cuenta como 0). Negativo si a es menor.</summary>
    public static int Compare(string? a, string? b)
    {
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var diff = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (diff != 0)
                return diff;
        }
        return 0;
    }

    /// <summary>
    /// El agente clásico de Windows 7 (decisión 78) se identifica con "-clasico" y nunca recibe el instalador del agente
    /// normal: no corre en su sistema.
    /// </summary>
    public static bool IsClassic(string? version) => version?.Contains("-clasico", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsOlder(string? installed, string available) => !IsClassic(installed) && Compare(installed, available) < 0;

    private static int[] Parts(string? version) =>
        (version ?? "").Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
}

/// <param name="Available">Versión publicada; null si el servidor no tiene instalador o llave de firma.</param>
public sealed record UpdateStatus(
    string? Available, string? Problem, bool AutoUpdate, int Total, int UpToDate, int Outdated, int Updating,
    IReadOnlyList<AgentVersionItem> OutdatedAgents);

public sealed record AgentVersionItem(int MachineId, string MachineName, string BranchName, string Version, bool Online, bool Updating);

public sealed record AutoUpdateInput(bool Activa);

using System.Text.RegularExpressions;

namespace MonitoreoTI.Application.Health;

/// <summary>
/// Reparar un servicio detenido desde Centinela (decisión 73): el comando que se manda al agente, qué nombres se aceptan y
/// qué servicios no merecen alerta porque Windows los detiene solo cuando terminan su trabajo.
/// </summary>
public static partial class ServiceRepairRules
{
    /// <summary>Intentos automáticos por servicio y equipo en 24 h: un servicio que se cae de nuevo no queda en bucle.</summary>
    public const int MaxAutomaticPerDay = 3;

    public const string AlertCodePrefix = "servicio:";

    public const string Language = "powershell";

    /// <summary>Nombre de servicio de Windows: letras, números y pocos signos; nunca comillas (va dentro del comando).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.\-$@ ]{1,128}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^\$ErrorActionPreference = 'Stop'\nStart-Service -Name '([^']+)'\n\(Get-Service -Name '\1'\)\.Status$")]
    private static partial Regex RepairScript();

    public static bool IsValidName(string? name) => name is not null && ValidName().IsMatch(name);

    /// <summary>"servicio:Spooler" → "Spooler"; null si la alerta no es de un servicio o el nombre no es válido.</summary>
    public static string? ServiceFromAlertCode(string? code) =>
        code is not null && code.StartsWith(AlertCodePrefix, StringComparison.Ordinal) && IsValidName(code[AlertCodePrefix.Length..])
            ? code[AlertCodePrefix.Length..]
            : null;

    /// <summary>Inicia el servicio y deja en la salida su estado final ("Running").</summary>
    public static string Script(string service)
    {
        if (!IsValidName(service))
            throw new ArgumentException("Nombre de servicio no válido.", nameof(service));
        return $"$ErrorActionPreference = 'Stop'\nStart-Service -Name '{service}'\n(Get-Service -Name '{service}').Status";
    }

    /// <summary>El servicio de un comando de reparación; null si el comando es otro.</summary>
    public static string? ServiceFromScript(string? content) =>
        content is not null && RepairScript().Match(content) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// Servicios que arrancan con Windows (o por evento) y se detienen solos al terminar: actualizadores, licencias,
    /// instalador de módulos. Que estén detenidos es normal; no generan alerta.
    /// </summary>
    private static readonly HashSet<string> SelfStopping = new(StringComparer.OrdinalIgnoreCase)
    {
        "sppsvc", "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService", "gupdate", "gupdatem", "MapsBroker",
        "RemoteRegistry", "TrustedInstaller", "BITS", "wuauserv", "UsoSvc", "WaaSMedicSvc", "sedsvc", "dmwappushservice",
    };

    public static bool StopsByItself(string name) =>
        SelfStopping.Contains(name)
        || name.StartsWith("GoogleUpdater", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("MozillaMaintenance", StringComparison.OrdinalIgnoreCase);
}

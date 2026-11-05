using FluentValidation;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Application.Network;

/// <summary>
/// Instalación remota del agente (decisión 77): el recolector de la subred entra a <c>\\IP\ADMIN$</c> con una cuenta del
/// dominio, copia el instalador y lo ejecuta como SYSTEM con un token de registro de un solo uso. La clave viaja
/// sellada para ese recolector, se usa en memoria y no se guarda en ningún lado.
/// </summary>
public static class RemoteInstallRules
{
    /// <summary>Primera versión del agente que sabe instalar en otro equipo.</summary>
    public static readonly Version InstallerVersion = new(0, 3, 7);

    public const int MaxPerBatch = 50;

    /// <summary>El token de registro de cada equipo vence en este tiempo.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(2);

    /// <summary>Una instalación que no avanza en este tiempo se da por perdida (recolector caído a mitad).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public const string Pending = "pendiente";
    public const string Installed = "instalado";
    public const string Registered = "registrado";
    public const string Failed = "error";
    public const string Skipped = "omitido";

    /// <summary>Lo que puede informar el recolector.</summary>
    public static readonly IReadOnlySet<string> AgentStates =
        new HashSet<string> { "conectando", "copiando", "instalando", Installed, Failed, Skipped };

    public static bool IsFinal(string status) => status is Registered or Failed or Skipped;

    public static bool SupportsInstall(string? agentVersion) =>
        Version.TryParse(agentVersion?.Split('-', '+')[0], out var version) && version >= InstallerVersion;

    /// <summary>"DOMINIO\usuario" o "usuario@dominio.local"; sin dominio no se acepta (las PCs están en el dominio).</summary>
    public static bool IsDomainAccount(string? account)
    {
        var a = account?.Trim();
        if (string.IsNullOrEmpty(a) || a.Length > 150 || a.Any(char.IsWhiteSpace))
            return false;
        var slash = a.IndexOf('\\');
        if (slash > 0)
            return slash < a.Length - 1 && a.IndexOf('\\', slash + 1) < 0;
        var at = a.IndexOf('@');
        return at > 0 && at < a.Length - 1 && a.IndexOf('@', at + 1) < 0;
    }
}

/// <summary>"Instalar agente": las IP de la subred y la cuenta del dominio (la clave no se guarda).</summary>
public sealed record RemoteInstallInput(IReadOnlyList<string> Ips, string Cuenta, string Clave);

public sealed record RemoteInstallItem(
    int Id, string Ip, string Status, string? Detail, string? OperatingSystem, string Account, string? UserName,
    int? MachineId, string? MachineName, DateTime CreatedAt, DateTime UpdatedAt);

public sealed class RemoteInstallInputValidator : AbstractValidator<RemoteInstallInput>
{
    public RemoteInstallInputValidator()
    {
        RuleFor(x => x.Ips).NotEmpty().WithMessage("Elija al menos una IP.")
            .Must(ips => ips is null || ips.Count <= RemoteInstallRules.MaxPerBatch)
            .WithMessage($"Hasta {RemoteInstallRules.MaxPerBatch} equipos por tanda.");
        RuleForEach(x => x.Ips).Must(ip => IpRules.TryParse(ip, out _)).WithMessage("Hay una IP que no es válida.");
        RuleFor(x => x.Cuenta).Must(RemoteInstallRules.IsDomainAccount)
            .WithMessage(@"Escriba la cuenta del dominio como DOMINIO\usuario o usuario@dominio.");
        RuleFor(x => x.Clave).NotEmpty().WithMessage("Escriba la clave de la cuenta.").MaximumLength(256);
    }
}

// ------------------------------------------------------------------ servidor ↔ recolector

/// <summary>"instalar_agente": lo que recibe el recolector (cuenta y token sellados para él).</summary>
public sealed record RemoteInstallRequest(
    int InstalacionId, string Ip, string Cuenta, string Servidor, string Token, AgentReleaseOffer Instalador);

public sealed record AgentReleaseOffer(string Version, string Url, string Sha256, long Tamano, string Firma);

/// <summary>"instalacion_estado": cómo va cada instalación.</summary>
public sealed record InstalacionEstadoData(int InstalacionId, string Estado, string? Detalle, string? Sistema);

public interface IRemoteInstallDispatcher
{
    /// <summary>false si el recolector no está conectado.</summary>
    Task<bool> SendAsync(int companyId, int agentId, RemoteInstallRequest request, CancellationToken cancellationToken);
}

public interface IRemoteInstallStatus
{
    Task SaveAsync(AgentIdentity identity, InstalacionEstadoData data, DateTime receivedAtUtc, CancellationToken cancellationToken);
}

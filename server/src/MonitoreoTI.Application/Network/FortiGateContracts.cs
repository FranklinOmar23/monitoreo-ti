using FluentValidation;
using MonitoreoTI.Application.Agents;

namespace MonitoreoTI.Application.Network;

/// <summary>
/// API REST del FortiGate (decisión 76), opcional: con un token de solo lectura el recolector lee las concesiones y el
/// rango del DHCP y los dispositivos que el FortiGate identificó (nombre y sistema). Sin token todo sigue por SNMP,
/// ping y NetBIOS. El token se guarda cifrado y viaja sellado para el recolector, igual que las claves SNMP.
/// </summary>
public static class FortiGateRules
{
    /// <summary>Primera versión del agente que entiende "probar_api" y lee la API.</summary>
    public static readonly Version ApiVersion = new(0, 3, 6);

    /// <summary>Primera versión del agente que lee los FortiSwitch por la API (decisión 80).</summary>
    public static readonly Version SwitchesVersion = new(0, 3, 13);

    public static bool SupportsSwitches(string? agentVersion) =>
        Version.TryParse(agentVersion?.Split('-', '+')[0], out var version) && version >= SwitchesVersion;

    /// <summary>Puerto de administración HTTPS que usa la empresa de prueba; el estándar es 443.</summary>
    public const int DefaultPort = 12443;

    public static bool SupportsApi(string? agentVersion) =>
        Version.TryParse(agentVersion?.Split('-', '+')[0], out var version) && version >= ApiVersion;

    /// <summary>"https://ip:puerto" sin barra ni ruta; null si no es una dirección http(s) válida.</summary>
    public static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath.Length > 1 || !string.IsNullOrEmpty(uri.Query))
            return null;
        return uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>La dirección que se sugiere en el formulario a partir de la IP de gestión.</summary>
    public static string? SuggestedUrl(string? managementIp) =>
        string.IsNullOrWhiteSpace(managementIp) ? null : $"https://{managementIp.Trim()}:{DefaultPort}";

    /// <summary>Los tokens de API de FortiOS son 30 caracteres alfanuméricos; se acepta un margen.</summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: >= 16 and <= 128 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>"Windows 10" a partir del nombre y la versión que da el FortiGate.</summary>
    public static string? OperatingSystem(string? name, string? version)
    {
        var n = name?.Trim();
        var v = version?.Trim();
        if (string.IsNullOrEmpty(n))
            return null;
        return string.IsNullOrEmpty(v) || n.Contains(v, StringComparison.OrdinalIgnoreCase) ? n : $"{n} {v}";
    }
}

// ------------------------------------------------------------------ servidor → recolector

/// <summary>
/// API del dispositivo en la lista del recolector: la dirección, el token sellado para ese agente y los FortiSwitch
/// registrados que dependen de ese FortiGate (decisión 80), para mandar un resultado de sondeo por cada uno.
/// </summary>
public sealed record CollectorApi(string Url, string Token, bool AceptarCertificado, IReadOnlyList<CollectorSwitch>? Switches = null);

public sealed record CollectorSwitch(int Id, string Serie);

/// <summary>"probar_api": el recolector consulta system/status con esta dirección y este token.</summary>
public sealed record ApiTestRequest(string SolicitudId, string Url, string Token, bool AceptarCertificado);

// ------------------------------------------------------------------ recolector → servidor

/// <summary>"api_prueba_resultado".</summary>
public sealed record ApiPruebaResultadoData(
    string SolicitudId, bool Ok, string? Error, string? Modelo, string? Version, string? Nombre, string? Serie, int? Ms);

/// <summary>
/// "fortigate_datos": lo que leyó de la API tras sondear el dispositivo. Si <see cref="Ok"/> es false solo trae el
/// motivo; con lectura parcial (un permiso que falta) trae lo que pudo y el aviso en <see cref="Error"/>.
/// Las listas en null no se leyeron (no se toca lo guardado).
/// </summary>
public sealed record FortiGateDatosData(
    int DispositivoId, bool Ok, string? Error,
    IReadOnlyList<FortiLeaseData>? Dhcp, IReadOnlyList<FortiDhcpServerData>? ServidoresDhcp, IReadOnlyList<FortiDeviceData>? Dispositivos);

/// <summary>Concesión del DHCP. <see cref="Vence"/> en segundos Unix; <see cref="Reservada"/> = dirección fija del DHCP.</summary>
public sealed record FortiLeaseData(string Ip, string? Mac, string? Nombre, long? Vence, string? Interfaz, bool? Reservada);

public sealed record FortiDhcpServerData(string? Interfaz, IReadOnlyList<FortiRangeData>? Rangos, IReadOnlyList<FortiReservedData>? Reservadas);

public sealed record FortiRangeData(string Desde, string Hasta);

public sealed record FortiReservedData(string Ip, string? Mac, string? Descripcion);

/// <summary>Dispositivo en línea según el FortiGate (identificación de dispositivos).</summary>
public sealed record FortiDeviceData(string? Ip, string? Mac, string? Nombre, string? Sistema, string? Interfaz);

// ------------------------------------------------------------------ FortiSwitch (decisión 80)

/// <summary>Un FortiSwitch que el FortiGate administra, como lo resume el recolector en "Detectar".</summary>
public sealed record DetectedSwitchData(
    string Serie, string? Nombre, string? Modelo, string? Ip, bool Conectado, int Puertos, string? Firmware);

/// <summary>"switches_detectados": la respuesta a "detectar_switches". <see cref="Campos"/>: los campos que trajo la API (diagnóstico).</summary>
public sealed record SwitchesDetectadosData(
    string SolicitudId, bool Ok, string? Error, IReadOnlyList<DetectedSwitchData>? Switches, IReadOnlyList<string>? Campos);

/// <summary>Lo que ve el panel: cada switch y, si ya está en Centinela, cuál es.</summary>
public sealed record DetectedSwitchItem(
    string Serial, string? Name, string? Model, string? Ip, bool Connected, int PortCount, string? Firmware,
    int? DeviceId, string? DeviceName);

public sealed record DetectSwitchesResult(bool Ok, string? Error, IReadOnlyList<DetectedSwitchItem> Switches, IReadOnlyList<string> Fields);

/// <summary>Registrar los FortiSwitch elegidos: van a la sucursal del FortiGate (gabinete y U se ubican después).</summary>
public sealed record RegisterSwitchesInput(IReadOnlyList<RegisterSwitchItem> Switches);

public sealed record RegisterSwitchItem(string Serie, string Nombre, string? Modelo, int Puertos, string? Ip = null);

public sealed class RegisterSwitchesInputValidator : AbstractValidator<RegisterSwitchesInput>
{
    public RegisterSwitchesInputValidator()
    {
        RuleFor(x => x.Switches).NotEmpty().WithMessage("Elija al menos un switch.");
        RuleForEach(x => x.Switches).ChildRules(s =>
        {
            s.RuleFor(x => x.Serie).NotEmpty().MaximumLength(50);
            s.RuleFor(x => x.Nombre).NotEmpty().WithMessage("Cada switch necesita un nombre.").MaximumLength(100);
            s.RuleFor(x => x.Modelo).MaximumLength(100);
            s.RuleFor(x => x.Puertos).InclusiveBetween(1, 128).WithMessage("El switch debe tener entre 1 y 128 puertos.");
        });
    }
}

// ------------------------------------------------------------------ servicios

/// <summary>Manda "probar_api" y espera la respuesta; null si el recolector no está conectado o no contestó a tiempo.</summary>
public interface IFortiGateApiTester
{
    Task<ApiTestResult?> TestAsync(int companyId, int agentId, string url, string sealedToken, bool acceptSelfSigned, CancellationToken cancellationToken);

    /// <summary>"Detectar" FortiSwitch (decisión 80); null si el recolector no está conectado o no contestó a tiempo.</summary>
    Task<SwitchesDetectadosData?> DetectSwitchesAsync(int companyId, int agentId, string url, string sealedToken, bool acceptSelfSigned, CancellationToken cancellationToken);
}

/// <summary>Guarda lo que el recolector leyó de la API.</summary>
public interface IFortiGateData
{
    Task SaveAsync(AgentIdentity identity, FortiGateDatosData data, DateTime receivedAtUtc, CancellationToken cancellationToken);
}

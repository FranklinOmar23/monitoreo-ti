using System.Globalization;

namespace MonitoreoTI.Application.Network;

/// <summary>
/// Direcciones IPv4 de una subred (decisión 72): rangos, en qué estado está cada dirección y cuál sugerir. Sin base de
/// datos: lo usan el servicio de subredes y las pruebas.
/// </summary>
public static class IpRules
{
    public const int MinPrefix = 22;
    public const int MaxPrefix = 30;

    /// <summary>Lo que vale "en uso": visto en las últimas 24 h.</summary>
    public static readonly TimeSpan InUseWindow = TimeSpan.FromHours(24);

    /// <summary>Un cambio de MAC más reciente que esto se avisa (equipo reemplazado o IP duplicada).</summary>
    public static readonly TimeSpan MacChangeWindow = TimeSpan.FromDays(7);

    /// <summary>El barrido con ping llegó con el agente 0.3.4 (el mismo que entiende "sondear_ahora").</summary>
    public static bool SupportsSweep(string? agentVersion) => SnmpRules.SupportsPollNow(agentVersion);

    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        var parts = text?.Trim().Split('.');
        if (parts is not { Length: 4 })
            return false;
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet))
                return false;
            value = (value << 8) | octet;
        }
        return true;
    }

    public static string ToText(uint value) =>
        $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";

    public static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    /// <summary>
    /// "192.168.3.0/24" → red y prefijo. Acepta una dirección dentro de la red ("192.168.3.10/24") y la lleva a la red.
    /// Solo prefijos de /22 a /30 (hasta 1022 direcciones por barrido).
    /// </summary>
    public static bool TryParseCidr(string? text, out uint network, out int prefix)
    {
        network = 0;
        prefix = 0;
        var parts = text?.Trim().Split('/');
        if (parts is not { Length: 2 } || !TryParse(parts[0], out var address)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
            || prefix is < MinPrefix or > MaxPrefix)
            return false;
        network = address & Mask(prefix);
        return true;
    }

    public static bool Contains(uint network, int prefix, uint ip) => (ip & Mask(prefix)) == network;

    /// <summary>Direcciones asignables: sin la de red ni la de difusión.</summary>
    public static IEnumerable<uint> Hosts(uint network, int prefix)
    {
        var broadcast = network | ~Mask(prefix);
        for (var ip = network + 1; ip < broadcast; ip++)
            yield return ip;
    }

    public static int HostCount(int prefix) => (1 << (32 - prefix)) - 2;

    /// <summary>
    /// Estado de una dirección. Lo reservado manda (aunque esté en uso, no se asigna). Lo visto en 24 h (por ping, ARP o
    /// el agente del equipo) está en uso; lo visto antes, dentro de los días de la subred, es "reciente" (una laptop
    /// apagada); lo que nadie usó en esos días está libre.
    /// </summary>
    public static IpState Classify(DateTime? lastSeen, bool reserved, bool knownMachine, int freeAfterDays, DateTime now)
    {
        if (reserved)
            return IpState.Reserved;
        if (lastSeen is DateTime seen && seen >= now - InUseWindow)
            return knownMachine ? IpState.InUse : IpState.Unidentified;
        if (lastSeen is DateTime old && old >= now - TimeSpan.FromDays(freeAfterDays))
            return IpState.Recent;
        return IpState.Free;
    }

    public static string Text(IpState state) => state switch
    {
        IpState.InUse => "en_uso",
        IpState.Unidentified => "sin_identificar",
        IpState.Reserved => "reservada",
        IpState.Recent => "reciente",
        _ => "libre",
    };

    /// <summary>La primera libre de verdad, empezando después de la puerta de enlace si va al principio de la red.</summary>
    public static uint? SuggestFree(IEnumerable<(uint Ip, IpState State)> addresses)
    {
        foreach (var (ip, state) in addresses.OrderBy(a => a.Ip))
            if (state == IpState.Free)
                return ip;
        return null;
    }

    /// <summary>Rango de reserva válido: dentro de la subred, sin las direcciones de red ni de difusión, desde ≤ hasta.</summary>
    public static bool IsValidRange(uint network, int prefix, uint from, uint to)
    {
        var broadcast = network | ~Mask(prefix);
        return from <= to && Contains(network, prefix, from) && Contains(network, prefix, to) && from > network && to < broadcast;
    }

    /// <summary>MAC de difusión o multicast: no es un equipo (la ARP local las trae).</summary>
    public static bool IsUnicastMac(string mac) =>
        mac != "00:00:00:00:00:00"
        && byte.TryParse(mac.AsSpan(0, Math.Min(2, mac.Length)), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first)
        && (first & 1) == 0;
}

public enum IpState
{
    Free,
    Recent,
    Unidentified,
    InUse,
    Reserved,
}

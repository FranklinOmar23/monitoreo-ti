using MonitoreoTI.Application.Health;

namespace MonitoreoTI.Application.Network;

/// <summary>
/// Reglas puras del recolector y de la correlación (secciones 11.1 y 11.3): qué interfaz SNMP es cada puerto
/// registrado, en qué estado queda, cuándo un puerto es troncal y qué MAC no vale la pena alertar.
/// </summary>
public static class SnmpRules
{
    /// <summary>Más MAC que esto en un puerto (sin ser host de VMs) lo hacen troncal (sección 11.3).</summary>
    public const int TrunkMacThreshold = 10;

    /// <summary>Primera versión del agente que entiende "sondear_ahora".</summary>
    public static readonly Version PollNowVersion = new(0, 3, 4);

    public static bool SupportsPollNow(string? agentVersion) =>
        Version.TryParse(agentVersion?.Split('-', '+')[0], out var version) && version >= PollNowVersion;

    // ethernetCsmacd, iso88023Csmacd, fastEther, gigabitEthernet; fibreChannel.
    private static readonly HashSet<int> PhysicalTypes = [6, 7, 62, 69, 117, 56];

    /// <summary>
    /// Interfaces físicas en el orden de ifIndex, que es el orden de los puertos del equipo (Gi1/0/1… y luego los
    /// uplinks). VLAN, port-channel, loopback y similares quedan fuera.
    /// </summary>
    public static IReadOnlyList<SnmpInterfazData> PhysicalInterfaces(IEnumerable<SnmpInterfazData> interfaces) =>
        interfaces.Where(i => i.Tipo is int type ? PhysicalTypes.Contains(type) : !LooksVirtual(i.Nombre))
            .OrderBy(i => i.Indice)
            .ToList();

    private static bool LooksVirtual(string? name) =>
        name is null || new[] { "vl", "po", "lo", "nu", "tu", "st", "mgmt", "cpu" }.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>El puerto número N (1, 2, …) es la N-ésima interfaz física. Sobran o faltan puertos si el modelo no coincide.</summary>
    public static IReadOnlyDictionary<int, SnmpInterfazData> MapToPortNumbers(IEnumerable<SnmpInterfazData> interfaces, IEnumerable<int> portNumbers)
    {
        var physical = PhysicalInterfaces(interfaces);
        return portNumbers.Order().Zip(physical).ToDictionary(p => p.First, p => p.Second);
    }

    /// <summary>Errores por hora entre dos lecturas de contadores; null si no hay lectura anterior o el contador se reinició.</summary>
    public static decimal? ErrorsPerHour(long? previousErrors, DateTime? previousAt, long? errors, DateTime at)
    {
        if (previousErrors is not long before || previousAt is not DateTime then || errors is not long now || now < before)
            return null;
        var hours = (decimal)(at - then).TotalHours;
        return hours < 1m / 60 ? null : Math.Round((now - before) / hours, 1);
    }

    /// <summary>
    /// Estado del puerto: apagado por configuración → deshabilitado; arriba → conectado (o con errores si pasa el
    /// umbral de advertencia); abajo → caído si ahí había un equipo conocido, libre si no.
    /// </summary>
    public static string PortStatus(SnmpInterfazData iface, decimal? errorsPerHour, decimal? errorWarning, bool hadKnownMachine)
    {
        if (iface.Admin == "down")
            return "deshabilitado";
        if (iface.Operativo == "up")
            return errorsPerHour is decimal rate && errorWarning is decimal limit && rate > limit ? "errores" : "conectado";
        return hadKnownMachine ? "caido" : "libre";
    }

    /// <summary>Troncal: LLDP ve otro equipo de red, o hay más de 10 MAC y no es un host de VMs.</summary>
    public static bool IsTrunk(bool hasNetworkNeighbor, int macCount, bool isVmHost) =>
        hasNetworkNeighbor || (macCount > TrunkMacThreshold && !isVmHost);

    /// <summary>MAC aleatoria (bit "administrada localmente"): celulares que cambian de MAC. Se agrupan y no alertan.</summary>
    public static bool IsRandomMac(string mac) =>
        mac.Length >= 2 && "26AEae".Contains(mac[1]);

    /// <summary>"Cisco IOS Software, C9200 …, Version 17.9.4, …" → "17.9.4".</summary>
    public static string? FirmwareFrom(string? sysDescr)
    {
        if (string.IsNullOrWhiteSpace(sysDescr))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(sysDescr, @"[Vv]ersion\s+([\w.()\-]+)");
        var text = match.Success ? match.Groups[1].Value.TrimEnd(',') : sysDescr.Trim();
        return text.Length > 100 ? text[..100] : text;
    }
}

// ------------------------------------------------------------------ alertas de red (sección 14)

public sealed record PortSnapshot(int Id, string Label, string Status, decimal? ErrorsPerHour, string? KnownMachine);

/// <param name="Polled">Tiene recolector, IP y claves: se espera que responda.</param>
/// <param name="Since">Desde cuándo se espera respuesta (último sondeo bueno o, si nunca respondió, alta del dispositivo).</param>
public sealed record DeviceSnapshot(int Id, string Name, bool Polled, DateTime Since, bool Responding, string? LastError, IReadOnlyList<PortSnapshot> Ports);

public static class NetworkHealth
{
    public static IReadOnlyList<Finding> Evaluate(DeviceSnapshot device, RuleSet rules, DateTime nowUtc)
    {
        var findings = new List<Finding>();
        if (!device.Polled)
            return findings;

        if (!device.Responding)
        {
            if (rules.For("switch_sin_respuesta") is { } rule)
            {
                var minutes = (decimal)(nowUtc - device.Since).TotalMinutes;
                var level = rule.Critical is decimal c && minutes >= c ? FindingLevel.Critical
                    : rule.Warning is decimal w && minutes >= w ? FindingLevel.Warning
                    : (FindingLevel?)null;
                if (level is { } l)
                {
                    findings.Add(new Finding(
                        "switch_sin_respuesta", l,
                        $"{device.Name} no responde por SNMP",
                        $"Lleva {Math.Floor(minutes)} min sin responder." + (device.LastError is null ? "" : $" Último error: {device.LastError}"),
                        "Revisa que el equipo esté encendido y en la red, y que el usuario y las claves SNMPv3 coincidan con su configuración.",
                        device.LastError, "switch_sin_respuesta"));
                }
            }
            // Sin respuesta, el estado de los puertos es viejo: no se evalúan.
            return findings;
        }

        var errors = rules.For("puerto_errores");
        var down = rules.For("puerto_caido");
        foreach (var port in device.Ports)
        {
            if (errors is not null && port.ErrorsPerHour is decimal rate)
            {
                var level = errors.Critical is decimal c && rate > c ? FindingLevel.Critical
                    : errors.Warning is decimal w && rate > w ? FindingLevel.Warning
                    : (FindingLevel?)null;
                if (level is { } l)
                {
                    findings.Add(new Finding(
                        $"puerto_errores:{port.Id}", l,
                        $"Errores en el puerto {port.Label}",
                        $"El puerto registra {rate:0} errores por hora" + (port.KnownMachine is null ? "." : $" (equipo {port.KnownMachine})."),
                        "Revisa el cable y el conector, o cambia el equipo a otro puerto: muchos errores suelen ser un cable dañado.",
                        $"SNMP · ifInErrors/ifOutErrors · {rate:0}/h", "puerto_errores"));
                }
            }
            if (down is not null && port.Status == "caido")
            {
                findings.Add(new Finding(
                    $"puerto_caido:{port.Id}", FindingLevel.Critical,
                    $"Se cayó el puerto de {port.KnownMachine ?? port.Label}",
                    $"El puerto {port.Label} donde estaba {port.KnownMachine ?? "un equipo conocido"} perdió el enlace.",
                    "Revisa que el equipo esté encendido y el cable conectado en ambos extremos.",
                    $"SNMP · ifOperStatus · {port.Label}", "puerto_caido"));
            }
        }
        return findings;
    }
}

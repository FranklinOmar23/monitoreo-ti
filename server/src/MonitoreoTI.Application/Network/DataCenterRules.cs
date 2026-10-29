using System.Text.RegularExpressions;

namespace MonitoreoTI.Application.Network;

/// <summary>Un equipo ya montado en el gabinete: U inferior y altura.</summary>
public sealed record RackSlot(int DeviceId, string Name, int PositionU, int HeightUnits);

/// <summary>Validación de U del gabinete (sección 11.2): sin solaparse y sin pasarse de la altura.</summary>
public static class RackLayout
{
    /// <returns>null si cabe; si no, el motivo en lenguaje simple.</returns>
    public static string? Check(int rackHeight, IEnumerable<RackSlot> placed, int positionU, int heightUnits, int? movingDeviceId = null)
    {
        if (heightUnits < 1)
            return null; // Organizadores y bandejas de 0 U no ocupan espacio.
        if (positionU < 1)
            return "La posición empieza en la U 1.";
        var top = positionU + heightUnits - 1;
        if (top > rackHeight)
            return $"No cabe: ocuparía hasta la U {top} y el gabinete tiene {rackHeight} U.";

        var clash = placed.FirstOrDefault(p => p.DeviceId != movingDeviceId && p.HeightUnits > 0
            && positionU <= p.PositionU + p.HeightUnits - 1 && p.PositionU <= top);
        return clash is null ? null : $"Las U {positionU}–{top} se cruzan con {clash.Name} (U {clash.PositionU}–{clash.PositionU + clash.HeightUnits - 1}).";
    }
}

/// <summary>Un puerto que se crea con el dispositivo.</summary>
public sealed record PlannedPort(int Number, string Name, string Type);

/// <summary>Puertos de un dispositivo nuevo según su modelo: cantidad_puertos y tipo_puertos (ej. "48 RJ45 + 4 SFP+").</summary>
public static partial class PortPlan
{
    [GeneratedRegex(@"(\d+)\s*x?\s*(RJ-?45|SFP28|SFP\+|SFP|QSFP\+?|fibra|consola|console)", RegexOptions.IgnoreCase)]
    private static partial Regex Groups();

    public static IReadOnlyList<PlannedPort> FromModel(int portCount, string? description, bool isPatchPanel)
    {
        var types = new List<string>();
        foreach (Match match in Groups().Matches(description ?? ""))
        {
            var type = match.Groups[2].Value.ToLowerInvariant().Replace("-", "") switch
            {
                "rj45" => "rj45",
                "sfp+" or "sfp28" or "qsfp" or "qsfp+" => "sfp_plus",
                "sfp" => "sfp",
                "fibra" => "fibra",
                _ => "consola",
            };
            types.AddRange(Enumerable.Repeat(type, int.Parse(match.Groups[1].Value)));
        }

        // Lo que la descripción no detalla se toma como RJ45; lo que sobra se ignora.
        var counters = new Dictionary<string, int>();
        return Enumerable.Range(1, Math.Max(portCount, 0)).Select(number =>
        {
            var type = number <= types.Count ? types[number - 1] : "rj45";
            counters[type] = counters.GetValueOrDefault(type) + 1;
            var name = isPatchPanel ? $"P{number}"
                : type switch
                {
                    "rj45" => number.ToString(),
                    "sfp" => $"SFP{counters[type]}",
                    "sfp_plus" => $"SFP+{counters[type]}",
                    "fibra" => $"F{counters[type]}",
                    _ => "Consola",
                };
            return new PlannedPort(number, name, type);
        }).ToList();
    }
}

// ------------------------------------------------------------------ ruta completa (W-65)

/// <summary>Un puerto con su dispositivo, para recorrer el cableado.</summary>
public sealed record PathPort(int PortId, string PortName, int DeviceId, string DeviceName, string DeviceType, bool IsActiveDevice, bool IsTrunk);

/// <summary>Un cable documentado o un enlace LLDP entre dos puertos, o entre un puerto y una roseta.</summary>
public sealed record PathLink(int PortAId, int? PortBId, int? OutletId, string? Label, bool Detected);

/// <summary>Un tramo de la ruta: roseta, puerto de patch panel, puerto de switch…</summary>
public sealed record PathHop(string Kind, int? DeviceId, string Name, string Detail, string? CableLabel);

/// <summary>
/// Ruta completa (sección 11.2): PC → roseta → patch panel → switch de acceso → uplink → core.
/// Un puerto de patch panel tiene dos cables (atrás a la roseta, adelante al switch). Desde el primer equipo activo
/// se sigue por sus puertos troncales hasta un equipo que ya no tenga a dónde subir.
/// </summary>
public static class CablePath
{
    public static IReadOnlyList<PathHop> FromOutlet(int outletId, string outletCode, IReadOnlyDictionary<int, PathPort> ports, IReadOnlyList<PathLink> links)
    {
        var hops = new List<PathHop> { new("roseta", null, outletCode, "Roseta", null) };
        var first = links.FirstOrDefault(l => l.OutletId == outletId);
        if (first is null || !ports.TryGetValue(first.PortAId, out var port))
            return hops;
        Walk(port, first, ports, links, hops);
        return hops;
    }

    public static IReadOnlyList<PathHop> FromPort(
        int portId, IReadOnlyDictionary<int, PathPort> ports, IReadOnlyList<PathLink> links, IReadOnlyDictionary<int, string> outletCodes)
    {
        if (!ports.TryGetValue(portId, out var port))
            return [];
        var hops = new List<PathHop>();
        // Si el puerto llega a una roseta, la ruta empieza ahí.
        var outlet = links.FirstOrDefault(l => l.OutletId is not null && l.PortAId == portId);
        if (outlet is not null)
            hops.Add(new PathHop("roseta", null, outletCodes.GetValueOrDefault(outlet.OutletId!.Value, "Roseta"), "Roseta", null));
        Walk(port, outlet, ports, links, hops);
        return hops;
    }

    private static void Walk(PathPort start, PathLink? arrivedBy, IReadOnlyDictionary<int, PathPort> ports, IReadOnlyList<PathLink> links, List<PathHop> hops)
    {
        var visitedPorts = new HashSet<int>();
        var visitedDevices = new HashSet<int>();
        var current = start;
        var via = arrivedBy;

        // 1) Pasivos (patch panels) hasta el primer equipo activo.
        while (true)
        {
            visitedPorts.Add(current.PortId);
            hops.Add(Hop(current, via));
            if (current.IsActiveDevice)
                break;
            var next = links.FirstOrDefault(l => l != via && l.PortBId is not null && Touches(l, current.PortId)
                && !visitedPorts.Contains(Other(l, current.PortId)));
            if (next is null || !ports.TryGetValue(Other(next, current.PortId), out var nextPort))
                return;
            current = nextPort;
            via = next;
        }

        // 2) Del equipo activo hacia arriba por sus troncales. "Arriba" es hacia un equipo con al menos tantas troncales
        //    (acceso → distribución → core): así, al llegar al core, la ruta no baja por otro uplink a un switch de acceso.
        var trunks = ports.Values.Where(p => p.IsTrunk).GroupBy(p => p.DeviceId).ToDictionary(g => g.Key, g => g.Count());
        while (visitedDevices.Add(current.DeviceId))
        {
            var mine = trunks.GetValueOrDefault(current.DeviceId);
            var uplink = ports.Values
                .Where(p => p.DeviceId == current.DeviceId && p.IsTrunk && p.PortId != current.PortId)
                .Select(p => (Port: p, Link: links.FirstOrDefault(l => l.PortBId is not null && Touches(l, p.PortId))))
                .Where(x => x.Link is not null && ports.TryGetValue(Other(x.Link!, x.Port.PortId), out var remote)
                    && !visitedDevices.Contains(remote.DeviceId) && trunks.GetValueOrDefault(remote.DeviceId) >= mine)
                .OrderBy(x => x.Port.PortId)
                .FirstOrDefault();
            if (uplink.Link is null)
                return;
            var remotePort = ports[Other(uplink.Link, uplink.Port.PortId)];
            hops.Add(new PathHop("uplink", current.DeviceId, $"{current.DeviceName} {uplink.Port.PortName}", "Uplink", uplink.Link.Label));
            hops.Add(Hop(remotePort, uplink.Link));
            current = remotePort;
        }
    }

    private static PathHop Hop(PathPort port, PathLink? via) => new(
        port.IsActiveDevice ? port.DeviceType : "patch_panel",
        port.DeviceId,
        $"{port.DeviceName} {port.PortName}",
        port.IsActiveDevice ? port.DeviceType : "Patch panel",
        via?.Label ?? (via is { Detected: true } ? "LLDP" : null));

    private static bool Touches(PathLink link, int portId) => link.PortAId == portId || link.PortBId == portId;
    private static int Other(PathLink link, int portId) => link.PortAId == portId ? link.PortBId!.Value : link.PortAId;
}

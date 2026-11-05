namespace MonitoreoTI.Application.Network;

/// <summary>
/// Qué clase de equipo cuelga de un puerto (decisión 74): el tipo de su ficha si Centinela lo conoce; si no, se deduce
/// del fabricante de la MAC (Yealink → teléfono IP, Hikvision → cámara…) y se marca como deducido.
/// </summary>
public static class EndpointRules
{
    private static readonly (string Category, string[] Vendors)[] ByVendor =
    [
        ("telefono", ["yealink", "grandstream", "polycom", "poly ", "fanvil", "snom", "avaya", "mitel", "htek", "obihai", "aastra"]),
        ("camara", ["hikvision", "dahua", "axis comm", "uniview", "zhejiang uniview", "hanwha", "vivotek", "ezviz", "reolink", "imou"]),
        ("impresora", ["epson", "canon", "brother", "lexmark", "ricoh", "xerox", "kyocera", "konica", "zebra", "sharp corp", "oki data"]),
        ("access_point", ["ubiquiti", "aruba", "ruckus", "tp-link", "cambium", "meraki", "mikrotik", "engenius"]),
    ];

    /// <param name="machineType">computadora, laptop, servidor, impresora u otro; null si la MAC no es de un equipo conocido.</param>
    public static (string Category, bool Inferred) Category(string? machineType, string? vendor)
    {
        switch (machineType)
        {
            case "computadora" or "laptop":
                return ("pc", false);
            case "servidor":
                return ("servidor", false);
            case "impresora":
                return ("impresora", false);
            case "otro":
                return ("otro", false);
        }
        var name = vendor?.ToLowerInvariant() ?? "";
        foreach (var (category, vendors) in ByVendor)
            if (vendors.Any(v => name.Contains(v)))
                return (category, true);
        return ("desconocido", false);
    }
}

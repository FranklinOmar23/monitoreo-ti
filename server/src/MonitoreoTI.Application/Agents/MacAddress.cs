namespace MonitoreoTI.Application.Agents;

public static class MacAddress
{
    /// <summary>Normaliza a AA:BB:CC:DD:EE:FF (acepta ':' o '-' como separador, cualquier capitalización).</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 17)
            return false;

        var upper = value.ToUpperInvariant();
        for (var i = 0; i < 17; i++)
        {
            var c = upper[i];
            if (i % 3 == 2)
            {
                if (c != ':' && c != '-')
                    return false;
            }
            else if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        normalized = upper.Replace('-', ':');
        return true;
    }
}

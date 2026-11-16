namespace MonitoreoTI.Application.Panel;

/// <summary>
/// "Actualizar ahora" en la ficha (decisión 85): el agente manda en el momento las métricas, los volúmenes, los
/// procesos y los servicios, sin esperar su horario (métricas cada minuto, procesos y servicios cada 5).
/// </summary>
public static class ReportNowRules
{
    /// <summary>Agente normal que entiende "reportar_ahora".</summary>
    public static readonly Version AgentVersion = new(0, 4, 2);

    /// <summary>Agente clásico (Windows 7/8) que lo entiende.</summary>
    public static readonly Version ClassicVersion = new(0, 2, 1);

    /// <summary>Entre dos pedidos al mismo equipo: cada pedido lanza consultas de WMI/PowerShell en el equipo.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    public static bool Supports(string? agentVersion)
    {
        if (!Version.TryParse(agentVersion?.Split('-', '+')[0], out var version))
            return false;
        var classic = agentVersion!.Contains("clasico", StringComparison.OrdinalIgnoreCase);
        return version >= (classic ? ClassicVersion : AgentVersion);
    }
}

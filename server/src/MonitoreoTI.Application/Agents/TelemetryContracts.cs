using System.Text.Json.Serialization;

namespace MonitoreoTI.Application.Agents;

// Cargas útiles de los mensajes del agente (sección 9). Los nombres de campo del JSON son parte del protocolo
// y están en shared/src/protocol.ts.

public sealed record MetricasData(decimal? Cpu, decimal? Ram, decimal? Disco, decimal? Temperatura);

public sealed record ComponenteData(
    string? Tipo,
    string? Descripcion,
    string? Capacidad,
    [property: JsonPropertyName("estado_salud")] string? EstadoSalud);

public sealed record InventarioHardwareData(
    string? Fabricante,
    string? Modelo,
    string? Serie,
    string? Tipo,
    [property: JsonPropertyName("sistema_operativo")] string? SistemaOperativo,
    [property: JsonPropertyName("ram_total_mb")] int? RamTotalMb,
    List<ComponenteData>? Componentes);

public sealed record ProgramaData(
    string? Nombre,
    string? Version,
    string? Editor,
    [property: JsonPropertyName("fecha_instalacion")] string? FechaInstalacion);

public sealed record InventarioSoftwareData(List<ProgramaData>? Programas);

public sealed record InterfazRedData(
    string? Nombre,
    string? Mac,
    string? Ip,
    string? Tipo,
    [property: JsonPropertyName("velocidad_mbps")] int? VelocidadMbps);

public sealed record InterfacesRedData(List<InterfazRedData>? Interfaces);

/// <summary>Muestra de métricas ya validada, lista para el escritor por lotes.</summary>
public sealed record MetricsSample(
    AgentIdentity Identity,
    DateTime TimestampUtc,
    decimal? Cpu,
    decimal? Ram,
    decimal? Disk,
    decimal? Temperature);

/// <summary>Destino en memoria de las métricas; un escritor en segundo plano las guarda por lotes.</summary>
public interface IMetricSink
{
    /// <summary>false si la cola está llena: el mensaje no se confirma y el agente lo reintenta luego.</summary>
    bool TryEnqueue(MetricsSample sample);
}

/// <summary>Inventarios que reporta el agente. Cada llamada usa su propio ámbito de empresa.</summary>
public interface IAgentInventory
{
    Task SaveHardwareAsync(AgentIdentity identity, InventarioHardwareData data, CancellationToken cancellationToken);
    Task SaveSoftwareAsync(AgentIdentity identity, InventarioSoftwareData data, CancellationToken cancellationToken);
    Task SaveInterfacesAsync(AgentIdentity identity, InterfacesRedData data, CancellationToken cancellationToken);
}

/// <summary>
/// Normaliza lo que dice el agente antes de tocar la base: recorta a las longitudes de las columnas,
/// descarta lo inválido y acota rangos. Un agente defectuoso no debe poder ensuciar los datos ni romper un lote.
/// </summary>
public static class TelemetryRules
{
    public const int MaxComponents = 200;
    public const int MaxPrograms = 5000;
    public const int MaxInterfaces = 100;

    public static decimal? Percent(decimal? value) =>
        value is >= 0 and <= 100 ? Math.Round(value.Value, 2) : null;

    public static decimal? Celsius(decimal? value) =>
        value is > 0 and < 150 ? Math.Round(value.Value, 2) : null;

    /// <summary>
    /// Fecha de la muestra: la del agente (con la cola local pueden llegar horas después de ocurrir)
    /// si es creíble; si no, la de recepción.
    /// </summary>
    public static DateTime SampleTime(DateTimeOffset agentTimestamp, DateTime nowUtc)
    {
        var utc = agentTimestamp.UtcDateTime;
        return utc > nowUtc.AddMinutes(5) || utc < nowUtc.AddDays(-7) ? nowUtc : utc;
    }

    public static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    public static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date) && date.Year >= 1990 && date <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2));
}

// ---------------------------------------------------------------- Fase 3: salud del equipo

public sealed record VolumenData(string? Unidad, string? Etiqueta, [property: JsonPropertyName("total_gb")] decimal TotalGb,
    [property: JsonPropertyName("libre_gb")] decimal LibreGb);

public sealed record VolumenesData(List<VolumenData>? Volumenes);

public sealed record ProcesoData(string? Nombre, int Pid, decimal Cpu, [property: JsonPropertyName("ram_mb")] decimal RamMb);

public sealed record ProcesosData(int Total, List<ProcesoData>? Procesos);

/// <param name="CodigoSalida">Solo si está detenido: 0 = terminó bien (se detuvo solo o a propósito), otro = falló.
/// Los agentes anteriores no lo envían.</param>
public sealed record ServicioData(string? Nombre, [property: JsonPropertyName("nombre_visible")] string? NombreVisible,
    string? Estado, string? Inicio, [property: JsonPropertyName("codigo_salida")] int? CodigoSalida = null);

public sealed record ServiciosData(List<ServicioData>? Servicios);

public sealed record EventoData(
    string? Registro,
    [property: JsonPropertyName("id_registro")] long IdRegistro,
    DateTimeOffset? Fecha,
    string? Origen,
    int Codigo,
    string? Nivel,
    string? Mensaje);

public sealed record EventosData(List<EventoData>? Eventos);

public sealed record DiscoSaludData(
    string? Modelo,
    string? Serie,
    [property: JsonPropertyName("tamano_gb")] int? TamanoGb,
    string? Salud,
    [property: JsonPropertyName("sectores_reasignados")] int? SectoresReasignados,
    [property: JsonPropertyName("sectores_pendientes")] int? SectoresPendientes,
    [property: JsonPropertyName("errores_lectura")] int? ErroresLectura,
    decimal? Desgaste,
    decimal? Temperatura,
    [property: JsonPropertyName("horas_encendido")] int? HorasEncendido);

public sealed record SaludDiscosData(List<DiscoSaludData>? Discos);

public sealed record AntivirusData(string? Nombre, bool Activo, bool Actualizado);

public sealed record SeguridadData(List<AntivirusData>? Antivirus, [property: JsonPropertyName("firewall_activo")] bool? FirewallActivo);

/// <summary>Datos de salud del equipo (Fase 3). Cada llamada usa su propio ámbito de empresa.</summary>
public interface IAgentHealthStore
{
    Task SaveVolumesAsync(AgentIdentity identity, VolumenesData data, CancellationToken cancellationToken);
    Task SaveProcessesAsync(AgentIdentity identity, ProcesosData data, CancellationToken cancellationToken);
    Task SaveServicesAsync(AgentIdentity identity, ServiciosData data, CancellationToken cancellationToken);
    Task SaveEventsAsync(AgentIdentity identity, EventosData data, CancellationToken cancellationToken);
    Task SaveDiskHealthAsync(AgentIdentity identity, SaludDiscosData data, CancellationToken cancellationToken);
    Task SaveSecurityAsync(AgentIdentity identity, SeguridadData data, CancellationToken cancellationToken);
}

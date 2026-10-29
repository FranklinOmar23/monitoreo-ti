namespace MonitoreoTI.Application.Health;

/// <summary>Cómo se presenta y edita una métrica de reglas_alerta.</summary>
/// <param name="LessIsWorse">El problema es quedar por debajo del umbral (tóner).</param>
/// <param name="HasThresholds">false = solo se activa o desactiva (servicio detenido, antivirus, puerto caído).</param>
/// <param name="Available">false = se evaluará cuando exista su módulo.</param>
public sealed record RuleMetric(
    string Key, string Name, string Description, string Unit, bool LessIsWorse, bool HasThresholds, bool HasDuration, bool Available);

/// <summary>Las métricas de la sección 14, en el orden en que se muestran.</summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleMetric> All =
    [
        new("disco", "Disco usado", "Espacio ocupado de cada unidad.", "%", false, true, false, true),
        new("cpu", "CPU sostenida", "Promedio del procesador durante la duración; el crítico exige el doble de tiempo.", "%", false, true, true, true),
        new("ram", "Memoria RAM", "Promedio de la memoria (mínimo 5 minutos sostenidos).", "%", false, true, true, true),
        new("temperatura", "Temperatura CPU", "Última lectura del sensor del procesador.", "°C", false, true, false, true),
        new("sin_reportar", "Sin reportar", "Minutos desde el último reporte del agente.", "min", false, true, false, true),
        new("errores_criticos", "Errores críticos del sistema", "Errores críticos de Windows en las últimas 24 horas.", "en 24 h", false, true, false, true),
        new("servicio_detenido", "Servicio automático detenido", "Un servicio que arranca con Windows estaba en ejecución y se detuvo.", "", false, false, false, true),
        new("antivirus", "Antivirus desactivado", "Ningún antivirus del equipo está activo.", "", false, false, false, true),
        new("toner", "Tóner", "Nivel de tóner de las impresoras.", "%", true, true, false, false),
        new("puerto_errores", "Errores en puerto", "Errores por hora en un puerto de switch.", "por hora", false, true, false, false),
        new("puerto_caido", "Puerto con equipo conocido caído", "El puerto de un equipo conocido se desconectó.", "", false, false, false, false),
        new("switch_sin_respuesta", "Switch sin responder SNMP", "Minutos sin respuesta SNMP del switch.", "min", false, true, false, false),
    ];

    public static RuleMetric? Find(string? key) => All.FirstOrDefault(m => m.Key == key);
}

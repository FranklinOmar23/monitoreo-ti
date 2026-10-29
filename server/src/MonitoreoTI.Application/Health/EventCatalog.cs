namespace MonitoreoTI.Application.Health;

/// <summary>Un error de Windows explicado para el técnico.</summary>
public sealed record EventExplanation(string Title, string Detail, string Advice);

/// <summary>
/// Traducción de los eventos de Windows más comunes a lenguaje simple (sección 8.2). Se amplía agregando entradas.
/// Clave: origen del evento (ProviderName, en minúsculas) y código; código null = cualquier código de ese origen.
/// </summary>
public static class EventCatalog
{
    private static readonly EventExplanation UnexpectedShutdown = new(
        "El equipo se apagó de forma inesperada",
        "Se apagó sin cerrar Windows. Suele ser la fuente de poder o un corte eléctrico sin UPS.",
        "Revisa la conexión al UPS y prueba la fuente de poder.");

    private static readonly EventExplanation DiskReadErrors = new(
        "Errores de lectura en el disco",
        "Windows no pudo leer partes del disco. Puede perder archivos o dejar de arrancar.",
        "Respalda los datos hoy y revisa la salud del disco.");

    private static readonly EventExplanation HardwareError = new(
        "Error de hardware detectado",
        "El procesador, la memoria o la placa informaron un error de hardware.",
        "Revisa la memoria RAM y la temperatura; si se repite, programa mantenimiento.");

    private static readonly EventExplanation BlueScreen = new(
        "Pantalla azul: el equipo se reinició por un error grave",
        "Windows se detuvo por un error grave y reinició el equipo.",
        "Revisa los controladores instalados recientemente y la memoria RAM.");

    private static readonly EventExplanation ServiceCrashed = new(
        "Un servicio se cerró inesperadamente",
        "Un servicio de Windows terminó por un error.",
        "Revisa si el servicio volvió a iniciar; si se repite, revisa el programa que lo instaló.");

    private static readonly EventExplanation ServiceFailedToStart = new(
        "Un servicio no pudo iniciar",
        "Un servicio configurado para iniciar no arrancó.",
        "Revisa la configuración del servicio y el programa que lo instaló.");

    private static readonly EventExplanation FileSystemErrors = new(
        "El sistema de archivos tiene errores",
        "Windows encontró daños en la estructura del disco.",
        "Ejecuta chkdsk en la próxima ventana de mantenimiento y revisa la salud del disco.");

    private static readonly EventExplanation AppCrashed = new(
        "Una aplicación se cerró por un error",
        "Un programa se cerró de forma inesperada.",
        "Si se repite con el mismo programa, actualízalo o reinstálalo.");

    private static readonly EventExplanation AppHang = new(
        "Una aplicación dejó de responder",
        "Un programa se quedó congelado y Windows lo cerró.",
        "Si se repite, revisa la memoria disponible y actualiza el programa.");

    private static readonly Dictionary<(string Source, int? Code), EventExplanation> Entries = new()
    {
        [("microsoft-windows-kernel-power", 41)] = UnexpectedShutdown,
        [("eventlog", 6008)] = UnexpectedShutdown,
        [("disk", 7)] = DiskReadErrors,
        [("disk", 51)] = DiskReadErrors,
        [("disk", 153)] = DiskReadErrors,
        [("microsoft-windows-whea-logger", null)] = HardwareError,
        [("microsoft-windows-wer-systemerrorreporting", 1001)] = BlueScreen,
        [("bugcheck", 1001)] = BlueScreen,
        [("service control manager", 7031)] = ServiceCrashed,
        [("service control manager", 7034)] = ServiceCrashed,
        [("service control manager", 7000)] = ServiceFailedToStart,
        [("service control manager", 7009)] = ServiceFailedToStart,
        [("service control manager", 7011)] = ServiceFailedToStart,
        [("ntfs", 55)] = FileSystemErrors,
        [("ntfs", 98)] = FileSystemErrors,
        [("microsoft-windows-ntfs", 55)] = FileSystemErrors,
        [("microsoft-windows-ntfs", 98)] = FileSystemErrors,
        [("application error", 1000)] = AppCrashed,
        [("application hang", 1002)] = AppHang,
    };

    public static EventExplanation? Explain(string? source, int code)
    {
        if (string.IsNullOrEmpty(source))
            return null;
        // Windows no es consistente con mayúsculas en los orígenes ("Ntfs", "disk"): se compara en minúsculas.
        var key = source.ToLowerInvariant();
        return Entries.GetValueOrDefault((key, code)) ?? Entries.GetValueOrDefault((key, null));
    }
}

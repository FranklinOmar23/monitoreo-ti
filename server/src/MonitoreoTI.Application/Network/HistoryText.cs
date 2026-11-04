using System.Text.Json;

namespace MonitoreoTI.Application.Network;

/// <summary>
/// Convierte un registro de auditoría del data center en texto para el historial (W-69): qué pasó, sobre qué y qué
/// campos cambiaron. Las referencias (sucursalId, salaId, gabineteId) se traducen a nombres con el resolvedor.
/// </summary>
public static class HistoryText
{
    public static readonly string[] Entities = ["salas", "gabinetes", "modelos_dispositivo", "dispositivos_red", "puertos", "puntos_red", "cableado"];

    private static readonly Dictionary<string, string> Titles = new()
    {
        ["sala.crear"] = "Sala creada",
        ["sala.editar"] = "Sala editada",
        ["sala.eliminar"] = "Sala eliminada",
        ["gabinete.crear"] = "Gabinete creado",
        ["gabinete.editar"] = "Gabinete editado",
        ["gabinete.eliminar"] = "Gabinete eliminado",
        ["modelo.crear"] = "Modelo agregado al catálogo",
        ["modelo.editar"] = "Modelo del catálogo editado",
        ["dispositivo.crear"] = "Dispositivo registrado",
        ["dispositivo.editar"] = "Dispositivo editado",
        ["dispositivo.mover"] = "Dispositivo movido",
        ["dispositivo.baja"] = "Dispositivo dado de baja",
        ["dispositivo.importar"] = "Importación de dispositivos",
        ["puerto.editar"] = "Puerto editado",
        ["puerto.asignar"] = "Puerto asignado a mano",
        ["puerto.estado"] = "Puerto cambió de estado",
        ["roseta.crear"] = "Roseta creada",
        ["roseta.editar"] = "Roseta editada",
        ["roseta.eliminar"] = "Roseta eliminada",
        ["roseta.importar"] = "Importación de rosetas",
        ["cable.crear"] = "Cable conectado",
        ["cable.eliminar"] = "Cable quitado",
        ["cable.importar"] = "Importación de cables",
        ["equipo.movido"] = "Equipo detectado en otro puerto",
    };

    private static readonly Dictionary<string, string> Fields = new()
    {
        ["nombre"] = "Nombre",
        ["ubicacion"] = "Ubicación",
        ["sucursalId"] = "Sucursal",
        ["salaId"] = "Sala",
        ["gabineteId"] = "Gabinete",
        ["alturaU"] = "Altura (U)",
        ["fila"] = "Fila",
        ["posicion"] = "Posición",
        ["posicionU"] = "U",
        ["activo"] = "Activo",
        ["marca"] = "Marca",
        ["modelo"] = "Modelo",
        ["tipo"] = "Tipo",
        ["puertos"] = "Puertos",
        ["tipoPuertos"] = "Tipos de puerto",
        ["ip"] = "IP de gestión",
        ["firmware"] = "Firmware",
        ["estado"] = "Estado",
        ["snmpUsuario"] = "Usuario SNMP",
        ["snmpCredencial"] = "Claves SNMP",
        ["recolectorId"] = "Recolector",
        ["vlan"] = "VLAN",
        ["hostVm"] = "Host de VMs",
        ["troncal"] = "Troncal",
        ["maquina"] = "Equipo",
        ["codigo"] = "Código",
        ["etiqueta"] = "Etiqueta",
        ["desde"] = "Desde",
        ["hasta"] = "Hasta",
        ["puerto"] = "Puerto",
        ["filas"] = "Filas",
    };

    public static string Title(string action) => Titles.GetValueOrDefault(action, action);

    /// <summary>El nombre del objeto según lo guardado (el después, o el antes si se eliminó).</summary>
    public static string? Subject(string? details)
    {
        var (before, after) = Read(details);
        foreach (var snapshot in new[] { after, before })
        {
            if (snapshot is not { } s)
                continue;
            if (Text(s, "desde") is { } from && Text(s, "hasta") is { } to)
                return $"{from} → {to}";
            if ((Text(s, "nombre") ?? Text(s, "codigo") ?? Text(s, "etiqueta")) is { } name)
                return name;
            if (Text(s, "marca") is { } brand && Text(s, "modelo") is { } model)
                return $"{brand} {model}";
        }
        return null;
    }

    /// <summary>
    /// Los campos que cambiaron en una edición o un movimiento. En un alta o una baja no hay "antes" o "después" que
    /// comparar y no se lista nada. <paramref name="resolve"/> traduce ids de referencia a nombres (o devuelve null).
    /// </summary>
    public static IReadOnlyList<HistoryChange> Changes(string? details, Func<string, string, string?> resolve)
    {
        var (before, after) = Read(details);
        if (before is not { } b || after is not { } a)
            return [];

        var changes = new List<HistoryChange>();
        foreach (var property in a.EnumerateObject())
        {
            var old = b.TryGetProperty(property.Name, out var value) ? value : default;
            var oldText = Display(property.Name, old, resolve);
            var newText = Display(property.Name, property.Value, resolve);
            if (oldText != newText)
                changes.Add(new HistoryChange(Fields.GetValueOrDefault(property.Name, property.Name), oldText, newText));
        }
        return changes;
    }

    private static string? Display(string field, JsonElement value, Func<string, string, string?> resolve) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.True => "Sí",
        JsonValueKind.False => "No",
        JsonValueKind.Number when field.EndsWith("Id", StringComparison.Ordinal) => resolve(field, value.GetRawText()) ?? $"#{value.GetRawText()}",
        JsonValueKind.String => value.GetString(),
        _ => value.GetRawText(),
    };

    private static string? Text(JsonElement snapshot, string name) =>
        snapshot.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static (JsonElement? Before, JsonElement? After) Read(string? details)
    {
        if (details is null)
            return (null, null);
        try
        {
            using var json = JsonDocument.Parse(details);
            JsonElement? Get(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v.Clone() : null;
            return (Get("antes"), Get("despues"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

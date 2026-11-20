namespace MonitoreoTI.Application.Health;

/// <summary>Lo que un técnico decidió sobre un grupo de errores (mismo origen y código), en un equipo o en toda la empresa.</summary>
/// <param name="MachineId">Null: vale para todos los equipos de la empresa (solo "ignorado").</param>
/// <param name="Ignored">true: ignorado (no cuenta ni alerta); false: resuelto hasta <paramref name="At"/>.</param>
public sealed record ErrorGroupMark(int? MachineId, string? Source, int? Code, bool Ignored, DateTime At);

/// <summary>Un servicio que el agente informó del equipo.</summary>
public sealed record KnownService(string Name, string? DisplayName);

/// <summary>
/// Errores del equipo agrupados y resueltos desde Centinela (decisión 91): qué ocurrencias siguen abiertas según lo que
/// marcó el técnico y qué servicio reparar.
/// </summary>
public static class ErrorGroupRules
{
    public const string Open = "abierto";
    public const string Resolved = "resuelto";
    public const string Reappeared = "reaparecio";
    public const string Ignored = "ignorado";

    /// <summary>Días de errores que se agrupan en la ficha por defecto (los mismos que cuenta la pestaña).</summary>
    public const int DefaultDays = 7;
    public const int MaxDays = 30;

    /// <summary>Lo que se reintenta reparar por grupo y equipo en una hora: un servicio que se cae enseguida no queda en bucle.</summary>
    public const int MaxFixesPerHour = 3;

    /// <summary>Mismo grupo: el origen sin distinguir mayúsculas (Windows no es consistente) y el mismo código.</summary>
    public static bool SameGroup(string? sourceA, int? codeA, string? sourceB, int? codeB) =>
        codeA == codeB && string.Equals(sourceA ?? "", sourceB ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// La marca que manda para el grupo en ese equipo: ignorar (en el equipo o en la empresa) gana a resuelto, y la del
    /// equipo a la de la empresa.
    /// </summary>
    public static ErrorGroupMark? MarkFor(IEnumerable<ErrorGroupMark> marks, int machineId, string? source, int? code)
    {
        var matching = marks.Where(m => (m.MachineId == machineId || m.MachineId is null) && SameGroup(m.Source, m.Code, source, code)).ToList();
        return matching.Where(m => m.Ignored).OrderByDescending(m => m.MachineId.HasValue).FirstOrDefault()
            ?? matching.Where(m => !m.Ignored && m.MachineId == machineId).OrderByDescending(m => m.At).FirstOrDefault();
    }

    /// <summary>La ocurrencia ya no cuenta: su grupo está ignorado, o resuelto después de que ocurrió.</summary>
    public static bool Hides(ErrorGroupMark? mark, DateTime at) =>
        mark is not null && (mark.Ignored || at <= mark.At);

    /// <summary>Estado del grupo para la ficha: abierto, resuelto, reapareció (ocurrió de nuevo tras resolverlo) o ignorado.</summary>
    public static string StateOf(ErrorGroupMark? mark, DateTime lastAt) =>
        mark is null ? Open : mark.Ignored ? Ignored : lastAt > mark.At ? Reappeared : Resolved;

    /// <summary>
    /// El servicio que hay que reparar para el error, solo entre los que el agente informó del equipo (nunca un nombre
    /// sacado del texto tal cual: el comando lo lleva entre comillas). SQL Server: el origen es el servicio. Otros: el
    /// servicio cuyo nombre visible (o nombre) aparece en el mensaje, el más largo si hay varios; sirve en cualquier idioma
    /// de Windows porque el mensaje trae el nombre visible del servicio.
    /// </summary>
    public static string? ServiceFor(EventExplanation? explanation, string? source, string? message, IEnumerable<KnownService> services)
    {
        if (explanation?.Fix != EventFix.RestartService)
            return null;
        var known = services.Where(s => ServiceRepairRules.IsValidName(s.Name)).ToList();
        if (explanation.ServiceIsSource)
            return known.FirstOrDefault(s => string.Equals(s.Name, source, StringComparison.OrdinalIgnoreCase))?.Name;
        if (string.IsNullOrWhiteSpace(message))
            return null;

        return known
            .SelectMany(s => new[] { (s.Name, Text: s.DisplayName), (s.Name, Text: (string?)s.Name) })
            .Where(x => x.Text is { Length: >= 3 } && ContainsWord(message, x.Text))
            .OrderByDescending(x => x.Text!.Length)
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    /// <summary>El texto aparece como palabra (no "Fax" dentro de "Fax2").</summary>
    private static bool ContainsWord(string message, string text)
    {
        var index = 0;
        while ((index = message.IndexOf(text, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(message[index - 1]);
            var end = index + text.Length;
            var after = end >= message.Length || !char.IsLetterOrDigit(message[end]);
            if (before && after)
                return true;
            index = end;
        }
        return false;
    }
}

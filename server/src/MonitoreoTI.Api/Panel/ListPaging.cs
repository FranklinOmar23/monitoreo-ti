using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Api.Panel;

/// <summary>
/// Paginación y filtros de los listados de catálogo (decisión 83, fase 2): sucursales, usuarios, scripts, modelos… Son
/// listas cortas que se arman en memoria, así que el filtro y la página se aplican sobre el resultado:
/// <list type="bullet">
/// <item><c>page</c> y <c>limit</c> (10 por defecto, máximo 100);</item>
/// <item>un filtro por cada campo de la fila con su mismo nombre (<c>?name=caja&amp;isActive=true</c>): texto por
/// coincidencia parcial, números, booleanos e ids exactos, fechas por día;</item>
/// <item><c>q</c> busca en todos los campos de texto;</item>
/// <item><c>fechaDesde</c>/<c>fechaHasta</c> sobre la fecha principal de la fila (inclusive, por día).</item>
/// </list>
/// Responde <c>{ data: { data, isThereNextPage, page, limit, total, totalPages } }</c>. Se respeta el orden de la lista.
/// </summary>
public static class ListPaging
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "page", "limit", "q", "fechaDesde", "fechaHasta" };
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static IResult Ok<T>(IEnumerable<T> items, HttpRequest request, Func<T, DateTime?>? date = null)
    {
        var query = request.Query;
        var from = Date(query["fechaDesde"]);
        var to = Date(query["fechaHasta"]);
        if (Paging.DateRangeError(from, to) is { } error)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["fechaHasta"] = [error] });

        var rows = items;
        var props = Properties.GetOrAdd(typeof(T), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => IsScalar(p.PropertyType)).ToArray());

        foreach (var (key, values) in query)
        {
            if (Reserved.Contains(key) || values.Count == 0 || string.IsNullOrWhiteSpace(values[0]))
                continue;
            var prop = props.FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (prop is null)
                continue;
            var wanted = values[0]!.Trim();
            rows = rows.Where(r => Matches(prop.GetValue(r), Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType, wanted)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(query["q"]))
        {
            var text = query["q"].ToString().Trim();
            var strings = props.Where(p => p.PropertyType == typeof(string)).ToArray();
            rows = rows.Where(r => strings.Any(p => (p.GetValue(r) as string)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        }

        date ??= MainDate<T>(props);
        if (date is not null)
        {
            var (start, end) = Paging.DateRange(from, to);
            if (start is not null)
                rows = rows.Where(r => date(r) is { } d && d >= start).ToList();
            if (end is not null)
                rows = rows.Where(r => date(r) is { } d && d < end).ToList();
        }

        var list = rows as IReadOnlyList<T> ?? rows.ToList();
        var (page, limit) = Paging.Normalize(Int(query["page"]), Int(query["limit"]));
        var data = list.Skip((page - 1) * limit).Take(limit).ToList();
        return Results.Ok(new { data = PagedResult<T>.Create(data, page * limit < list.Count, page, limit, list.Count) });
    }

    /// <summary>La fecha principal de la fila, si no se indicó: la de alta, inicio o última vez visto.</summary>
    private static Func<T, DateTime?>? MainDate<T>(PropertyInfo[] props)
    {
        foreach (var name in new[] { "CreatedAt", "StartedAt", "RequestedAt", "LastSeenAt", "At" })
        {
            var prop = props.FirstOrDefault(p => p.Name == name && (Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType) == typeof(DateTime));
            if (prop is not null)
                return row => (DateTime?)prop.GetValue(row);
        }
        return null;
    }

    private static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateOnly)
            || t == typeof(Guid);
    }

    private static bool Matches(object? value, Type type, string wanted)
    {
        if (value is null)
            return false;
        if (type == typeof(string))
            return ((string)value).Contains(wanted, StringComparison.OrdinalIgnoreCase);
        if (type == typeof(bool))
            return bool.TryParse(wanted, out var b) && (bool)value == b;
        if (type == typeof(DateTime))
            return DateTime.TryParse(wanted, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && ((DateTime)value).Date == d.Date;
        if (type == typeof(DateOnly))
            return DateOnly.TryParse(wanted, CultureInfo.InvariantCulture, out var day) && (DateOnly)value == day;
        return string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? Date(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static int? Int(string? text) => int.TryParse(text, out var n) ? n : null;
}

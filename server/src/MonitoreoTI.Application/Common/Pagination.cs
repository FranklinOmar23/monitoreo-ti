namespace MonitoreoTI.Application.Panel;

/// <summary>
/// Una página de un listado (decisión 83). Sale como { data: [...], isThereNextPage, page, limit, total, totalPages }
/// dentro del "data" de la respuesta.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Data, bool IsThereNextPage, int Page, int Limit, int Total, int TotalPages)
{
    public PagedResult<TOut> Map<TOut>(Func<T, TOut> map) =>
        new(Data.Select(map).ToList(), IsThereNextPage, Page, Limit, Total, TotalPages);

    public static PagedResult<T> Create(IReadOnlyList<T> data, bool hasNext, int page, int limit, int total) =>
        new(data, hasNext, page, limit, total, total == 0 ? 0 : (int)Math.Ceiling(total / (double)limit));
}

/// <summary>Valores comunes de los listados: 10 por página por defecto y como mucho 100.</summary>
public static class Paging
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 100;

    public static (int Page, int Limit) Normalize(int? page, int? limit) =>
        (page is > 0 ? page.Value : 1, limit is > 0 ? Math.Min(limit.Value, MaxLimit) : DefaultLimit);

    /// <summary>fechaDesde inclusive; fechaHasta inclusive por día completo (hasta antes del día siguiente).</summary>
    public static (DateTime? From, DateTime? ToExclusive) DateRange(DateTime? from, DateTime? to) =>
        (from?.Date is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
         to?.Date is { } t ? DateTime.SpecifyKind(t.AddDays(1), DateTimeKind.Utc) : null);

    public static string? DateRangeError(DateTime? from, DateTime? to) =>
        from is not null && to is not null && from.Value.Date > to.Value.Date ? "fechaDesde no puede ser posterior a fechaHasta." : null;
}

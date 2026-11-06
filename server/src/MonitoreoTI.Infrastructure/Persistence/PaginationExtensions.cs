using Microsoft.EntityFrameworkCore;
using MonitoreoTI.Application.Panel;

namespace MonitoreoTI.Infrastructure.Persistence;

/// <summary>Una fila de la página con el total del filtro, para traer las dos cosas en el mismo viaje a la base.</summary>
public sealed class PageRow<T>
{
    public T Item { get; set; } = default!;
    public int Total { get; set; }
}

public static class PaginationExtensions
{
    /// <summary>
    /// La página pedida de una consulta ya ordenada (con un orden estable): trae limit + 1 filas para saber si hay
    /// siguiente y el total como subconsulta de la misma sentencia (un solo viaje a la base, decisión 83).
    /// </summary>
    public static async Task<PagedResult<T>> ToPageAsync<T>(this IQueryable<T> ordered, int page, int limit, CancellationToken ct)
    {
        var rows = await ordered
            .Select(x => new PageRow<T> { Item = x, Total = ordered.Count() })
            .Skip((page - 1) * limit)
            .Take(limit + 1)
            .ToListAsync(ct);
        var hasNext = rows.Count > limit;
        var items = rows.Take(limit).Select(r => r.Item).ToList();
        // Una página más allá del final no trae filas (ni el total): se cuenta aparte.
        var total = rows.Count > 0 ? rows[0].Total : page > 1 ? await ordered.CountAsync(ct) : 0;
        return PagedResult<T>.Create(items, hasNext, page, limit, total);
    }
}

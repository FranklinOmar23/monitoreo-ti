import type { Page } from '@monitoreoti/shared';

/**
 * Paginador de los listados (decisión 83): «Mostrando 21–30 de 115 · ‹ Anterior · Página 3 de 12 · Siguiente ›».
 * Solo aparece cuando hay más de una página.
 */
export function Pager<T>({
  data,
  noun,
  onPage,
}: {
  data: Page<T> | undefined;
  /** "equipos", "alertas"… */
  noun: string;
  onPage: (page: number) => void;
}) {
  if (!data || data.totalPages <= 1) return null;
  const first = (data.page - 1) * data.limit + 1;
  const last = first + data.data.length - 1;
  return (
    <nav className="pager" aria-label={`Páginas de ${noun}`}>
      <span className="muted">
        Mostrando {data.data.length > 0 ? `${first}–${last}` : '0'} de {data.total} {noun}
      </span>
      <button type="button" className="btn btn-outline" disabled={data.page <= 1} onClick={() => onPage(data.page - 1)}>
        ‹ Anterior
      </button>
      <span className="pager-page" aria-current="page">
        Página {data.page} de {data.totalPages}
      </span>
      <button type="button" className="btn btn-outline" disabled={!data.isThereNextPage} onClick={() => onPage(data.page + 1)}>
        Siguiente ›
      </button>
    </nav>
  );
}

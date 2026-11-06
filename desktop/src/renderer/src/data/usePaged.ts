import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { Page, Paged } from '@monitoreoti/shared';

/** Tamaño de página de los listados (decisión 83). */
export const PAGE_SIZE = 10;

/**
 * Un listado paginado de 10 en 10: guarda la página, vuelve a la primera cuando cambia la clave (los filtros) y
 * mantiene la página anterior en pantalla mientras llega la siguiente.
 */
export function usePaged<T>(
  key: readonly unknown[],
  fetch: (params: { page: number; limit: number }) => Promise<Paged<T>>,
  options: { enabled?: boolean; refetchInterval?: number | false | ((data: Page<T> | undefined) => number | false) } = {},
) {
  const [page, setPage] = useState(1);
  const filterKey = JSON.stringify(key);
  const [lastKey, setLastKey] = useState(filterKey);
  if (filterKey !== lastKey) {
    setLastKey(filterKey);
    setPage(1);
  }
  const { refetchInterval } = options;
  const query = useQuery({
    queryKey: [...key, { page }],
    queryFn: () => fetch({ page, limit: PAGE_SIZE }),
    placeholderData: keepPreviousData,
    enabled: options.enabled,
    refetchInterval:
      typeof refetchInterval === 'function' ? (q) => refetchInterval(q.state.data?.data) : refetchInterval,
  });
  return { query, data: query.data?.data, items: query.data?.data.data ?? [], setPage };
}

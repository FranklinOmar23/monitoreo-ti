/**
 * Una página de un listado (decisión 83): 10 por página por defecto, máximo 100. `total` y `totalPages` son del filtro
 * completo; `isThereNextPage` dice si hay más después de esta.
 */
export interface Page<T> {
  data: T[];
  isThereNextPage: boolean;
  page: number;
  limit: number;
  total: number;
  totalPages: number;
}

/** Parámetros comunes de los listados: página, tamaño y rango de fechas (yyyy-mm-dd, ambos inclusive). */
export interface PageParams {
  page?: number;
  limit?: number;
  fechaDesde?: string | null;
  fechaHasta?: string | null;
}

/**
 * Listados de catálogo (fase 2): además de la página y las fechas, `q` busca en todos los textos y cualquier campo
 * de la fila filtra con su mismo nombre (`{ name: 'caja', isActive: true }`).
 */
export type ListParams = PageParams & { q?: string } & Record<string, string | number | boolean | null | undefined>;

/** Respuesta de los listados sin datos extra: { data: Page }. */
export interface Paged<T> {
  data: Page<T>;
}

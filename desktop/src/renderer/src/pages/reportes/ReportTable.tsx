import { useMemo, useState, type ReactNode } from 'react';
import { EmptyState } from '@components/AsyncState';
import { Pager } from '@components/Pager';

export interface Column<T> {
  key: string;
  label: string;
  /** Valor para ordenar; sin él, la columna no se ordena. */
  sort?: (row: T) => string | number | null | undefined;
  render: (row: T) => ReactNode;
  /** Números alineados a la derecha. */
  numeric?: boolean;
}

interface ReportTableProps<T> {
  title?: string;
  rows: readonly T[];
  columns: readonly Column<T>[];
  rowKey: (row: T, index: number) => string | number;
  /** Orden inicial (por omisión, el del servidor). */
  initialSort?: { key: string; dir: 'asc' | 'desc' };
  /** Filtro "Solo con problemas": qué fila cuenta como problema. */
  problem?: (row: T) => boolean;
  /** "equipos", "programas"… */
  noun: string;
  empty: string;
  pageSize?: number;
}

const collator = new Intl.Collator('es', { numeric: true, sensitivity: 'base' });

function compare(a: string | number | null | undefined, b: string | number | null | undefined): number {
  // Los vacíos siempre al final, sea cual sea el sentido.
  if (a === null || a === undefined || a === '') return b === null || b === undefined || b === '' ? 0 : 1;
  if (b === null || b === undefined || b === '') return -1;
  return typeof a === 'number' && typeof b === 'number' ? a - b : collator.compare(String(a), String(b));
}

/**
 * Tabla de reporte (decisión 92): columnas ordenables, cabecera fija, "Solo con problemas" y páginas de 50. Las filas de
 * otras páginas siguen en el documento, ocultas en pantalla, para que la hoja impresa lleve el reporte completo.
 */
export function ReportTable<T>({
  title,
  rows,
  columns,
  rowKey,
  initialSort,
  problem,
  noun,
  empty,
  pageSize = 50,
}: ReportTableProps<T>) {
  const [sort, setSort] = useState(initialSort ?? null);
  const [onlyProblems, setOnlyProblems] = useState(false);
  const [page, setPage] = useState(1);

  const problems = useMemo(() => (problem ? rows.filter(problem).length : 0), [rows, problem]);
  const sorted = useMemo(() => {
    const base = onlyProblems && problem ? rows.filter(problem) : [...rows];
    const column = sort && columns.find((c) => c.key === sort.key);
    if (!sort || !column?.sort) return base;
    const get = column.sort;
    const sign = sort.dir === 'asc' ? 1 : -1;
    return base
      .map((row, i) => ({ row, i }))
      .sort((x, y) => {
        const a = get(x.row);
        const b = get(y.row);
        const empty = a === null || a === undefined || a === '' || b === null || b === undefined || b === '';
        return (empty ? compare(a, b) : compare(a, b) * sign) || x.i - y.i;
      })
      .map((x) => x.row);
  }, [rows, columns, sort, onlyProblems, problem]);

  const totalPages = Math.max(1, Math.ceil(sorted.length / pageSize));
  const current = Math.min(page, totalPages);
  const first = (current - 1) * pageSize;

  const toggle = (key: string) => {
    setSort((s) => (s?.key === key ? { key, dir: s.dir === 'asc' ? 'desc' : 'asc' } : { key, dir: 'asc' }));
    setPage(1);
  };

  return (
    <section className="table-card report-table">
      <header className="report-table-head no-print">
        {title && <h2>{title}</h2>}
        <span className="muted mono">
          {sorted.length} {noun}
        </span>
        {problem && (
          <label className="report-toggle">
            <input
              type="checkbox"
              checked={onlyProblems}
              onChange={(e) => {
                setOnlyProblems(e.target.checked);
                setPage(1);
              }}
            />
            Solo con problemas
            <span className="report-toggle-count mono" data-zero={problems === 0 || undefined}>
              {problems}
            </span>
          </label>
        )}
      </header>
      {title && <h2 className="print-only report-print-title">{title}</h2>}
      <div className="report-table-scroll">
        <table className="data-table data-table-compact">
          <thead>
            <tr>
              {columns.map((c) => {
                const active = sort?.key === c.key;
                return (
                  <th
                    key={c.key}
                    data-numeric={c.numeric || undefined}
                    aria-sort={active ? (sort.dir === 'asc' ? 'ascending' : 'descending') : undefined}
                  >
                    {c.sort ? (
                      <button
                        type="button"
                        className="report-sort"
                        data-active={active || undefined}
                        onClick={() => toggle(c.key)}
                      >
                        {c.label}
                        <span aria-hidden="true">{active ? (sort.dir === 'asc' ? '↑' : '↓') : '↕'}</span>
                      </button>
                    ) : (
                      c.label
                    )}
                  </th>
                );
              })}
            </tr>
          </thead>
          <tbody>
            {sorted.map((row, i) => (
              <tr
                key={rowKey(row, i)}
                className={i < first || i >= first + pageSize ? 'report-row-hidden' : undefined}
                data-problem={problem?.(row) || undefined}
              >
                {columns.map((c) => (
                  <td key={c.key} data-numeric={c.numeric || undefined}>
                    {c.render(row)}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {sorted.length === 0 && <EmptyState title={onlyProblems ? `Ningún problema: todo en orden` : empty} />}
      <div className="no-print">
        <Pager
          noun={noun}
          onPage={setPage}
          data={{
            data: sorted.slice(first, first + pageSize),
            page: current,
            limit: pageSize,
            total: sorted.length,
            totalPages,
            isThereNextPage: current < totalPages,
          }}
        />
      </div>
    </section>
  );
}

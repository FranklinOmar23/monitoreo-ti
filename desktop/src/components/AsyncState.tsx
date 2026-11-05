import type { CSSProperties, ReactNode } from 'react';

/**
 * Forma del esqueleto mientras cargan los datos (decisión 71): imita lo que va a aparecer para que la página no salte.
 * - block: un bloque y una línea (lo genérico).
 * - table: cabecera y filas de tabla.
 * - kpis: tira de indicadores.
 * - list: filas con punto, título y detalle (alertas, eventos).
 * - detail: ficha (título, metadatos, pestañas, indicadores y dos columnas).
 * - dashboard: tira de indicadores, gráfica y dos listas.
 * - cards: rejilla de tarjetas (sucursales, scripts).
 */
export type LoadingVariant = 'block' | 'table' | 'kpis' | 'list' | 'detail' | 'dashboard' | 'cards';

interface LoadingProps {
  label?: string;
  variant?: LoadingVariant;
  /** Filas (table, list) o tarjetas (cards). */
  rows?: number;
  /** Columnas de la tabla. */
  columns?: number;
}

/** Línea gris con brillo; `w` es el ancho (por ejemplo "60%"). */
export function Skeleton({ w, h, className }: { w?: string; h?: number; className?: string }) {
  const style: CSSProperties = {};
  if (w) style.width = w;
  if (h) style.height = h;
  return <span className={`skeleton${className ? ` ${className}` : ''}`} style={style} aria-hidden="true" />;
}

/** Ancho de cada celda, variado para que no parezca una cuadrícula. */
const CELL_WIDTHS = ['72%', '48%', '86%', '40%', '64%', '56%', '78%', '44%'];

function TableSkeleton({ rows, columns }: { rows: number; columns: number }) {
  const cols = Array.from({ length: columns }, (_, i) => i);
  return (
    <div className="sk-table">
      <div className="sk-table-head">
        {cols.map((c) => (
          <Skeleton key={c} w={c === 0 ? '40%' : '55%'} h={8} />
        ))}
      </div>
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className="sk-table-row" style={{ '--i': r } as CSSProperties}>
          {cols.map((c) => (
            <Skeleton key={c} w={CELL_WIDTHS[(r + c * 3) % CELL_WIDTHS.length]} h={c === 0 ? 12 : 10} />
          ))}
        </div>
      ))}
    </div>
  );
}

function KpiSkeleton({ count = 5 }: { count?: number }) {
  return (
    <div className="sk-kpis">
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="sk-kpi" style={{ '--i': i } as CSSProperties}>
          <Skeleton w="45%" h={8} />
          <Skeleton w="38%" h={24} />
          <Skeleton w="62%" h={8} />
        </div>
      ))}
    </div>
  );
}

function ListSkeleton({ rows }: { rows: number }) {
  return (
    <div className="sk-panel">
      <div className="sk-panel-head">
        <Skeleton w="28%" h={10} />
      </div>
      {Array.from({ length: rows }, (_, r) => (
        <div key={r} className="sk-list-row" style={{ '--i': r } as CSSProperties}>
          <span className="sk-dot skeleton" aria-hidden="true" />
          <div className="sk-lines">
            <Skeleton w={CELL_WIDTHS[r % CELL_WIDTHS.length]} h={11} />
            <Skeleton w="34%" h={8} />
          </div>
          <Skeleton w="48px" h={8} />
        </div>
      ))}
    </div>
  );
}

function PanelSkeleton({ height = 220 }: { height?: number }) {
  return (
    <div className="sk-panel">
      <div className="sk-panel-head">
        <Skeleton w="22%" h={10} />
        <Skeleton w="18%" h={10} />
      </div>
      <div className="sk-chart" style={{ height }}>
        {Array.from({ length: 7 }, (_, i) => (
          <span
            key={i}
            className="skeleton sk-bar"
            style={{ height: `${35 + ((i * 37) % 55)}%`, '--i': i } as CSSProperties}
            aria-hidden="true"
          />
        ))}
      </div>
    </div>
  );
}

function CardsSkeleton({ count }: { count: number }) {
  return (
    <div className="sk-cards">
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="sk-panel sk-card" style={{ '--i': i } as CSSProperties}>
          <Skeleton w="50%" h={12} />
          <Skeleton w="30%" h={8} />
          <Skeleton w="100%" h={6} />
          <div className="sk-card-foot">
            <Skeleton w="22%" h={8} />
            <Skeleton w="22%" h={8} />
            <Skeleton w="22%" h={8} />
          </div>
        </div>
      ))}
    </div>
  );
}

function DetailSkeleton() {
  return (
    <>
      <div className="sk-detail-head">
        <span className="sk-dot skeleton" aria-hidden="true" />
        <div className="sk-lines">
          <Skeleton w="220px" h={20} />
          <Skeleton w="520px" h={8} />
        </div>
      </div>
      <div className="sk-actions">
        {[96, 84, 84, 104, 32].map((w, i) => (
          <Skeleton key={i} w={`${w}px`} h={32} />
        ))}
      </div>
      <div className="sk-tabs">
        {[64, 88, 56, 120, 112, 40].map((w, i) => (
          <Skeleton key={i} w={`${w}px`} h={8} />
        ))}
      </div>
      <KpiSkeleton count={5} />
      <div className="sk-two">
        <PanelSkeleton height={200} />
        <ListSkeleton rows={3} />
      </div>
    </>
  );
}

function Body({ variant, rows, columns }: Required<Omit<LoadingProps, 'label'>>) {
  switch (variant) {
    case 'table':
      return <TableSkeleton rows={rows} columns={columns} />;
    case 'kpis':
      return <KpiSkeleton />;
    case 'list':
      return <ListSkeleton rows={rows} />;
    case 'cards':
      return <CardsSkeleton count={rows} />;
    case 'detail':
      return <DetailSkeleton />;
    case 'dashboard':
      return (
        <>
          <KpiSkeleton />
          <div className="sk-two sk-two-wide">
            <PanelSkeleton />
            <ListSkeleton rows={3} />
          </div>
          <div className="sk-two">
            <ListSkeleton rows={4} />
            <TableSkeleton rows={4} columns={4} />
          </div>
        </>
      );
    default:
      return (
        <>
          <span className="skeleton" aria-hidden="true" />
          <span className="skeleton skeleton-short" aria-hidden="true" />
        </>
      );
  }
}

/** Marcador de carga (skeleton) con la forma de lo que viene. */
export function Loading({ label = 'Cargando…', variant = 'block', rows, columns = 6 }: LoadingProps) {
  const count = rows ?? (variant === 'cards' ? 3 : variant === 'list' ? 5 : 8);
  return (
    <div className={`state state-loading sk sk-${variant}`} role="status" aria-live="polite" aria-busy="true">
      <Body variant={variant} rows={count} columns={columns} />
      <span className="visually-hidden">{label}</span>
    </div>
  );
}

interface ErrorStateProps {
  error: unknown;
  onRetry?: () => void;
}

export function ErrorState({ error, onRetry }: ErrorStateProps) {
  const message = error instanceof Error ? error.message : 'No se pudieron cargar los datos.';
  return (
    <div className="state state-error" role="alert">
      <p>{message}</p>
      {onRetry && (
        <button type="button" className="btn btn-outline" onClick={onRetry}>
          Reintentar
        </button>
      )}
    </div>
  );
}

interface EmptyStateProps {
  title: string;
  children?: ReactNode;
}

export function EmptyState({ title, children }: EmptyStateProps) {
  return (
    <div className="state state-empty">
      <strong>{title}</strong>
      {children && <p className="muted">{children}</p>}
    </div>
  );
}

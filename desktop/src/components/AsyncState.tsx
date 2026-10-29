import type { ReactNode } from 'react';

interface LoadingProps {
  label?: string;
}

/** Marcador de carga (skeleton) del tamaño de una tarjeta. */
export function Loading({ label = 'Cargando…' }: LoadingProps) {
  return (
    <div className="state state-loading" role="status" aria-live="polite">
      <span className="skeleton" aria-hidden="true" />
      <span className="skeleton skeleton-short" aria-hidden="true" />
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

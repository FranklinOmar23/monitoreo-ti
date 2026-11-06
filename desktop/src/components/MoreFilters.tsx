import { useState, type ReactNode } from 'react';

/**
 * «Más filtros» (decisión 83): los filtros menos usados de un listado, plegados bajo un botón que dice cuántos hay
 * puestos. Siempre incluye el rango de fechas.
 */
export function MoreFilters({
  active,
  onClear,
  dateLabel,
  from,
  to,
  onDates,
  children,
}: {
  /** Cuántos filtros de este panel están puestos (incluidas las fechas). */
  active: number;
  onClear: () => void;
  /** Qué fecha filtra: "Fecha de alta", "Creada"… */
  dateLabel: string;
  from: string;
  to: string;
  onDates: (from: string, to: string) => void;
  children?: ReactNode;
}) {
  const [open, setOpen] = useState(active > 0);
  const invalid = from !== '' && to !== '' && from > to;
  return (
    <div className="more-filters">
      <button type="button" className="btn btn-outline" aria-expanded={open} onClick={() => setOpen(!open)}>
        Más filtros{active > 0 ? ` (${active})` : ''}
      </button>
      {open && (
        <div className="more-filters-panel" role="group" aria-label="Más filtros">
          {children}
          <label className="field">
            <span>{dateLabel} desde</span>
            <input type="date" value={from} max={to || undefined} onChange={(e) => onDates(e.target.value, to)} />
          </label>
          <label className="field">
            <span>{dateLabel} hasta</span>
            <input type="date" value={to} min={from || undefined} onChange={(e) => onDates(from, e.target.value)} />
          </label>
          {invalid && (
            <p className="api-test-result is-error more-filters-wide" role="alert">
              La fecha «desde» es posterior a «hasta».
            </p>
          )}
          {active > 0 && (
            <button type="button" className="link-quiet more-filters-wide" onClick={onClear}>
              Quitar estos filtros
            </button>
          )}
        </div>
      )}
    </div>
  );
}

/** Cuenta los valores puestos (texto no vacío, números, booleanos). */
export const countActive = (...values: unknown[]) => values.filter((v) => v !== '' && v !== null && v !== undefined).length;

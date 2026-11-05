import { useState } from 'react';

export interface StackedColumn {
  label: string;
  highlight?: boolean;
  sano: number;
  advertencia: number;
  critico: number;
  desconectado: number;
}

interface StackedBarsProps {
  columns: readonly StackedColumn[];
  max: number;
}

const SEGMENTS = [
  ['desconectado', 'Desconectado'],
  ['critico', 'Crítico'],
  ['advertencia', 'Advertencia'],
  ['sano', 'Sano'],
] as const;

/** Orden del detalle: de lo más grave a lo sano, como se lee un tablero de operaciones. */
const DETAIL_ORDER = [
  ['critico', 'Crítico'],
  ['advertencia', 'Advertencia'],
  ['sano', 'Sano'],
  ['desconectado', 'Desconectado'],
] as const;

/**
 * Barras apiladas por día; cada columna describe sus valores para lectores de pantalla.
 * Interactiva (decisión 71): al pasar el mouse o enfocar una columna, las demás se atenúan y aparece el detalle del
 * día con cada estado y el total.
 */
export function StackedBars({ columns, max }: StackedBarsProps) {
  const [active, setActive] = useState<number | null>(null);
  const ticks = [max, Math.round(max * 0.75), Math.round(max * 0.5), Math.round(max * 0.25), 0];

  return (
    <div className="stacked">
      <div className="stacked-axis mono" aria-hidden="true">
        {ticks.map((tick, i) => (
          <span key={i}>{tick}</span>
        ))}
      </div>
      <div
        className="stacked-plot chart-interactive"
        data-active={active === null ? undefined : true}
        onPointerLeave={() => setActive(null)}
      >
        {columns.map((column, index) => {
          const total = SEGMENTS.reduce((sum, [key]) => sum + column[key], 0);
          return (
            <div
              key={column.label}
              className="stacked-col"
              role="img"
              tabIndex={0}
              data-hover={active === index || undefined}
              aria-label={`${column.label}: ${SEGMENTS.map(([key, name]) => `${column[key]} ${name}`).join(', ')}`}
              onPointerEnter={() => setActive(index)}
              onFocus={() => setActive(index)}
              onBlur={() => setActive(null)}
            >
              <div className="stacked-bar">
                {SEGMENTS.map(([key]) => (
                  <span
                    key={key}
                    className="stacked-seg"
                    data-status={key}
                    style={{ height: `${(column[key] / max) * 100}%` }}
                  />
                ))}
              </div>
              <span className={`stacked-label mono${column.highlight ? ' is-today' : ''}`}>{column.label}</span>
              {active === index && (
                <div
                  className="chart-tooltip stacked-tooltip"
                  data-side={index > columns.length / 2 ? 'left' : 'right'}
                  aria-hidden="true"
                >
                  <span className="chart-tooltip-title mono">{column.label}</span>
                  {DETAIL_ORDER.map(([key, name]) => (
                    <span
                      key={key}
                      className="chart-tooltip-row"
                      data-status={key}
                      data-zero={column[key] === 0 || undefined}
                    >
                      <i />
                      <strong className="mono">{column[key]}</strong>
                      <span>{name}</span>
                    </span>
                  ))}
                  <span className="chart-tooltip-note mono">{total} en total</span>
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

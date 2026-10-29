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

/** Barras apiladas por día; cada columna describe sus valores para lectores de pantalla. */
export function StackedBars({ columns, max }: StackedBarsProps) {
  const ticks = [max, Math.round(max * 0.75), Math.round(max * 0.5), Math.round(max * 0.25), 0];

  return (
    <div className="stacked">
      <div className="stacked-axis mono" aria-hidden="true">
        {ticks.map((tick) => (
          <span key={tick}>{tick}</span>
        ))}
      </div>
      <div className="stacked-plot">
        {columns.map((column) => (
          <div
            key={column.label}
            className="stacked-col"
            role="img"
            aria-label={`${column.label}: ${SEGMENTS.map(([key, name]) => `${column[key]} ${name}`).join(', ')}`}
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
          </div>
        ))}
      </div>
    </div>
  );
}

import { useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';

export interface LineSeries {
  name: string;
  /** Clave de color: coincide con una clase `.series-<color>` de la hoja de estilos. */
  color: 'cpu' | 'ram';
  values: readonly number[];
  /** Relleno suave bajo la línea. */
  area?: boolean;
}

interface LineChartProps {
  series: readonly LineSeries[];
  xLabels: readonly string[];
  /** Línea punteada de umbral (porcentaje). */
  threshold?: number;
  /** Etiqueta de cada punto para el detalle al pasar el mouse ("09:15", "lun 3"); sin ella, el número de muestra. */
  pointLabels?: readonly string[];
}

const WIDTH = 700;
const HEIGHT = 230;
const PAD = { top: 10, right: 8, bottom: 4, left: 36 };

/**
 * Gráfica de líneas 0-100% con umbral. Escala fija para que dos equipos se comparen a simple vista.
 * Interactiva (decisión 71): una línea vertical sigue al mouse y se ajusta al punto más cercano, con el valor de cada
 * serie en ese momento; con el teclado, las flechas recorren los puntos (Inicio y Fin van a los extremos).
 */
export function LineChart({ series, xLabels, threshold, pointLabels }: LineChartProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [active, setActive] = useState<number | null>(null);
  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const count = Math.max(...series.map((s) => s.values.length), 0);
  const xAt = (i: number) => PAD.left + (i / Math.max(count - 1, 1)) * plotW;
  const y = (value: number) => PAD.top + plotH - (Math.min(100, Math.max(0, value)) / 100) * plotH;
  const grid = [0, 25, 50, 75, 100];

  const pathOf = (values: readonly number[]) =>
    values.map((value, i) => `${i === 0 ? 'M' : 'L'}${xAt(i).toFixed(1)} ${y(value).toFixed(1)}`).join(' ');

  const indexFromPointer = (event: PointerEvent<SVGSVGElement>) => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0 || count === 0) return null;
    const svgX = ((event.clientX - rect.left) / rect.width) * WIDTH;
    const ratio = (svgX - PAD.left) / plotW;
    return Math.min(count - 1, Math.max(0, Math.round(ratio * (count - 1))));
  };

  const onKeyDown = (event: KeyboardEvent<SVGSVGElement>) => {
    if (count === 0) return;
    const current = active ?? count - 1;
    const next =
      event.key === 'ArrowLeft'
        ? Math.max(0, current - 1)
        : event.key === 'ArrowRight'
          ? Math.min(count - 1, current + 1)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? count - 1
              : null;
    if (event.key === 'Escape') setActive(null);
    if (next === null) return;
    event.preventDefault();
    setActive(next);
  };

  const label = (i: number) => pointLabels?.[i] ?? `Muestra ${i + 1} de ${count}`;
  const leftPct = active === null ? 0 : (xAt(active) / WIDTH) * 100;

  return (
    <div className="line-chart chart-interactive">
      <svg
        ref={svgRef}
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        tabIndex={0}
        aria-label={`Gráfica de ${series.map((s) => s.name).join(' y ')}. Use las flechas para recorrer los valores.`}
        onPointerMove={(e) => setActive(indexFromPointer(e))}
        onPointerLeave={() => setActive(null)}
        onFocus={() => setActive((a) => a ?? count - 1)}
        onBlur={() => setActive(null)}
        onKeyDown={onKeyDown}
      >
        {grid.map((tick) => (
          <g key={tick}>
            <line className="chart-grid" x1={PAD.left} x2={WIDTH - PAD.right} y1={y(tick)} y2={y(tick)} />
            <text className="chart-tick" x={PAD.left - 6} y={y(tick) + 3} textAnchor="end">
              {tick}%
            </text>
          </g>
        ))}
        {threshold !== undefined && (
          <line className="chart-threshold" x1={PAD.left} x2={WIDTH - PAD.right} y1={y(threshold)} y2={y(threshold)} />
        )}
        {series.map((s) => {
          const line = pathOf(s.values);
          const lastX = xAt(s.values.length - 1);
          return (
            <g key={s.name} className={`series-${s.color}`}>
              {s.area && <path className="chart-area" d={`${line} L${lastX} ${y(0)} L${PAD.left} ${y(0)} Z`} />}
              <path className="chart-line" d={line} pathLength={1} />
            </g>
          );
        })}
        {active !== null && (
          <g className="chart-cursor" aria-hidden="true">
            <line x1={xAt(active)} x2={xAt(active)} y1={PAD.top} y2={PAD.top + plotH} />
            {series.map((s) =>
              s.values[active] === undefined ? null : (
                <circle
                  key={s.name}
                  className={`chart-point series-${s.color}`}
                  cx={xAt(active)}
                  cy={y(s.values[active]!)}
                  r={4.5}
                />
              ),
            )}
          </g>
        )}
      </svg>
      {active !== null && (
        <div
          className="chart-tooltip"
          data-side={leftPct > 62 ? 'left' : 'right'}
          style={{ left: `${leftPct}%` }}
          role="status"
          aria-live="polite"
        >
          <span className="chart-tooltip-title mono">{label(active)}</span>
          {series.map((s) => (
            <span key={s.name} className={`chart-tooltip-row series-${s.color}`}>
              <i aria-hidden="true" />
              <strong className="mono">
                {s.values[active] === undefined ? '—' : `${Math.round(s.values[active]! * 10) / 10}%`}
              </strong>
              <span>{s.name}</span>
            </span>
          ))}
          {threshold !== undefined && series.some((s) => (s.values[active] ?? 0) >= threshold) && (
            <span className="chart-tooltip-note">Sobre el umbral de {threshold}%</span>
          )}
        </div>
      )}
      <div className="line-chart-x mono" aria-hidden="true">
        {xLabels.map((text, index) => (
          <span key={index}>{text}</span>
        ))}
      </div>
    </div>
  );
}

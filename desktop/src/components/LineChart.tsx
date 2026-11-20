import { useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';

export interface LineSeries {
  name: string;
  /** Clave de color: coincide con una clase `.series-<color>` de la hoja de estilos. */
  color: 'cpu' | 'ram';
  /** null = sin dato en ese punto: la línea se corta en vez de caer a 0. */
  values: readonly (number | null)[];
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
  /**
   * Momento de cada punto (ms). Con él, el eje X es de tiempo real: los ratos sin muestras (equipo apagado, agente
   * detenido) quedan como una franja "sin datos" en vez de pegarse y aparentar continuidad.
   */
  times?: readonly number[];
  /** Inicio y fin del eje de tiempo (por defecto, la primera y la última muestra). */
  domain?: readonly [number, number];
  /** Separación entre muestras a partir de la cual se considera que faltan datos. */
  maxGapMs?: number;
}

interface Gap {
  from: number;
  to: number;
}

const WIDTH = 700;
const HEIGHT = 230;
const PAD = { top: 10, right: 8, bottom: 4, left: 36 };

/**
 * Gráfica de líneas 0-100% con umbral. Escala fija para que dos equipos se comparen a simple vista.
 * Interactiva (decisión 71): una línea vertical sigue al mouse y se ajusta al punto más cercano, con el valor de cada
 * serie en ese momento; con el teclado, las flechas recorren los puntos (Inicio y Fin van a los extremos).
 */
export function LineChart({ series, xLabels, threshold, pointLabels, times, domain, maxGapMs }: LineChartProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [active, setActive] = useState<number | null>(null);
  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const count = Math.max(...series.map((s) => s.values.length), 0);
  const timed = !!times && times.length === count && count > 0;
  const d0 = timed ? (domain?.[0] ?? times[0]!) : 0;
  const d1 = timed ? (domain?.[1] ?? times[count - 1]!) : 0;
  const span = Math.max(d1 - d0, 1);
  const xOfTime = (t: number) => PAD.left + Math.min(1, Math.max(0, (t - d0) / span)) * plotW;
  const xAt = (i: number) => (timed ? xOfTime(times[i]!) : PAD.left + (i / Math.max(count - 1, 1)) * plotW);
  const y = (value: number) => PAD.top + plotH - (Math.min(100, Math.max(0, value)) / 100) * plotH;
  const grid = [0, 25, 50, 75, 100];

  // Hueco antes del punto i: falta de muestras en el tiempo.
  const gapBefore = (i: number) => timed && maxGapMs !== undefined && i > 0 && times[i]! - times[i - 1]! > maxGapMs;
  const gaps: Gap[] = [];
  if (timed && maxGapMs !== undefined) {
    if (times[0]! - d0 > maxGapMs) gaps.push({ from: d0, to: times[0]! });
    for (let i = 1; i < count; i++) if (gapBefore(i)) gaps.push({ from: times[i - 1]!, to: times[i]! });
    if (d1 - times[count - 1]! > maxGapMs) gaps.push({ from: times[count - 1]!, to: d1 });
  }

  /** Tramos continuos de una serie (índices), cortados donde no hay dato o faltan muestras. */
  const segmentsOf = (values: readonly (number | null)[]) => {
    const segments: number[][] = [];
    let current: number[] = [];
    values.forEach((value, i) => {
      if (value === null || gapBefore(i)) {
        if (current.length) segments.push(current);
        current = [];
      }
      if (value !== null) current.push(i);
    });
    if (current.length) segments.push(current);
    return segments;
  };
  const pathOf = (values: readonly (number | null)[], segment: readonly number[]) =>
    segment.map((i, k) => `${k === 0 ? 'M' : 'L'}${xAt(i).toFixed(1)} ${y(values[i]!).toFixed(1)}`).join(' ');

  const indexFromPointer = (event: PointerEvent<SVGSVGElement>) => {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0 || count === 0) return null;
    const svgX = ((event.clientX - rect.left) / rect.width) * WIDTH;
    if (!timed) {
      const ratio = (svgX - PAD.left) / plotW;
      return Math.min(count - 1, Math.max(0, Math.round(ratio * (count - 1))));
    }
    // Eje de tiempo: el punto más cercano (búsqueda binaria; los tiempos vienen ordenados).
    let lo = 0;
    let hi = count - 1;
    while (lo < hi) {
      const mid = (lo + hi) >> 1;
      if (xAt(mid) < svgX) lo = mid + 1;
      else hi = mid;
    }
    return lo > 0 && Math.abs(xAt(lo - 1) - svgX) <= Math.abs(xAt(lo) - svgX) ? lo - 1 : lo;
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
        {gaps.map((gap) => {
          const x1 = xOfTime(gap.from);
          const w = xOfTime(gap.to) - x1;
          if (w < 2) return null;
          return (
            <g key={gap.from} className="chart-gap">
              <rect x={x1} y={PAD.top} width={w} height={plotH} />
              {w > 54 && (
                <text x={x1 + w / 2} y={PAD.top + plotH / 2} textAnchor="middle">
                  SIN DATOS
                </text>
              )}
            </g>
          );
        })}
        {threshold !== undefined && (
          <line className="chart-threshold" x1={PAD.left} x2={WIDTH - PAD.right} y1={y(threshold)} y2={y(threshold)} />
        )}
        {series.map((s) => (
          <g key={s.name} className={`series-${s.color}`}>
            {segmentsOf(s.values).map((segment) => {
              const line = pathOf(s.values, segment);
              const firstX = xAt(segment[0]!);
              const lastX = xAt(segment[segment.length - 1]!);
              return (
                <g key={segment[0]}>
                  {s.area && segment.length > 1 && (
                    <path className="chart-area" d={`${line} L${lastX} ${y(0)} L${firstX} ${y(0)} Z`} />
                  )}
                  {segment.length === 1 ? (
                    <circle className="chart-dot" cx={firstX} cy={y(s.values[segment[0]!]!)} r={2} />
                  ) : (
                    <path className="chart-line" d={line} pathLength={1} />
                  )}
                </g>
              );
            })}
          </g>
        ))}
        {active !== null && (
          <g className="chart-cursor" aria-hidden="true">
            <line x1={xAt(active)} x2={xAt(active)} y1={PAD.top} y2={PAD.top + plotH} />
            {series.map((s) =>
              s.values[active] == null ? null : (
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
                {s.values[active] == null ? '—' : `${Math.round(s.values[active]! * 10) / 10}%`}
              </strong>
              <span>{s.name}</span>
            </span>
          ))}
          {threshold !== undefined && series.some((s) => (s.values[active] ?? -1) >= threshold) && (
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

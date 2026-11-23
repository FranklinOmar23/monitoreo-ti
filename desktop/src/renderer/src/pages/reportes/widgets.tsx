import { useState, type ReactNode } from 'react';
import { Icon } from '@components/Icon';

/** Piezas visuales de los reportes (decisión 92): KPI con variación, hallazgos, mapa de calor y barras. */

export type Tone = 'neutral' | 'sano' | 'advertencia' | 'critico';

/** Si subir es bueno (disponibilidad), malo (alertas) o ninguna de las dos (equipos, sesiones). */
export type Better = 'up' | 'down' | 'none';

const nf = new Intl.NumberFormat('es-DO', { maximumFractionDigits: 1 });
export const num = (value: number) => nf.format(value);

/** "2026-11-03" → "3 nov". */
export function shortDay(iso: string): string {
  return new Date(`${iso.slice(0, 10)}T12:00:00`)
    .toLocaleDateString('es-DO', { day: 'numeric', month: 'short' })
    .replace('.', '');
}

/** Variación frente al periodo anterior: en puntos si es un porcentaje, en % si es un conteo. */
export function deltaText(value: number, previous: number, unit?: string): { text: string; diff: number } {
  const diff = Math.round((value - previous) * 10) / 10;
  if (diff === 0) return { text: 'igual', diff };
  const arrow = diff > 0 ? '▲' : '▼';
  if (unit === '%') return { text: `${arrow} ${num(Math.abs(diff))} pts`, diff };
  if (unit === 'h') return { text: `${arrow} ${num(Math.abs(diff))} h`, diff };
  if (previous === 0) return { text: `${arrow} ${num(Math.abs(diff))}`, diff };
  return { text: `${arrow} ${num(Math.abs(Math.round((diff / previous) * 100)))}%`, diff };
}

export function deltaTone(diff: number, better: Better): Tone {
  if (diff === 0 || better === 'none') return 'neutral';
  return diff > 0 === (better === 'up') ? 'sano' : 'critico';
}

/** Línea de tendencia que se ajusta al rango de la serie (los huecos se saltan). */
export function Trend({ values }: { values: readonly (number | null)[] }) {
  const points = values.map((v, i) => [i, v] as const).filter((p): p is readonly [number, number] => p[1] !== null);
  if (points.length < 2) return null;
  const ys = points.map((p) => p[1]);
  let min = Math.min(...ys);
  let max = Math.max(...ys);
  if (max - min < 1) {
    min -= 1;
    max += 1;
  }
  const width = 100;
  const height = 28;
  const last = Math.max(values.length - 1, 1);
  const path = points
    .map(
      ([i, v], n) =>
        `${n === 0 ? 'M' : 'L'}${((i / last) * width).toFixed(1)},${(height - 2 - ((v - min) / (max - min)) * (height - 4)).toFixed(1)}`,
    )
    .join(' ');
  return (
    <svg className="report-trend" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" aria-hidden="true">
      <path d={`${path} L${width},${height} L0,${height} Z`} className="report-trend-area" />
      <path d={path} className="report-trend-line" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

interface DeltaKpiProps {
  label: string;
  value: number | null;
  unit?: string;
  /** Valor del periodo anterior; sin él (o en la foto de hoy) no hay variación. */
  previous?: number | null;
  better?: Better;
  series?: readonly (number | null)[];
  tone?: Tone;
  note?: ReactNode;
  /** Con él, la tarjeta es un botón (abre el reporte). */
  onOpen?: () => void;
}

/** KPI del reporte: el número, cuánto cambió frente al periodo anterior y su tendencia. */
export function DeltaKpi({
  label,
  value,
  unit,
  previous,
  better = 'none',
  series,
  tone = 'neutral',
  note,
  onOpen,
}: DeltaKpiProps) {
  const delta = value !== null && previous !== null && previous !== undefined ? deltaText(value, previous, unit) : null;
  const content = (
    <>
      <span className="eyebrow">
        <span className="kpi-dot" aria-hidden="true" />
        {label}
      </span>
      <span className="kpi-value">
        <strong>{value === null ? '—' : num(value)}</strong>
        {unit && value !== null && <span className="kpi-unit mono">{unit}</span>}
        {delta && (
          <span
            className="report-delta mono"
            data-tone={deltaTone(delta.diff, better)}
            title={`Periodo anterior: ${num(previous!)}${unit ?? ''}`}
          >
            {delta.text}
          </span>
        )}
      </span>
      {series && series.length > 1 && <Trend values={series} />}
      {note && <span className="kpi-note">{note}</span>}
    </>
  );
  return onOpen ? (
    <button type="button" className="card kpi report-kpi report-kpi-link" data-tone={tone} onClick={onOpen}>
      {content}
    </button>
  ) : (
    <div className="card kpi report-kpi" data-tone={tone}>
      {content}
    </div>
  );
}

export interface Insight {
  level: 'critico' | 'advertencia' | 'sano' | 'info';
  text: ReactNode;
}

/** Hallazgos: las conclusiones del reporte en frases cortas, de lo más grave a lo menos. */
export function Insights({ items }: { items: readonly Insight[] }) {
  if (items.length === 0) return null;
  const order = { critico: 0, advertencia: 1, info: 2, sano: 3 };
  return (
    <section className="report-insights" aria-label="Hallazgos">
      <h2 className="eyebrow">Hallazgos</h2>
      <ul>
        {[...items]
          .sort((a, b) => order[a.level] - order[b.level])
          .map((item, i) => (
            <li key={i} data-level={item.level}>
              <span className="report-insight-mark" aria-hidden="true" />
              <span>{item.text}</span>
            </li>
          ))}
      </ul>
    </section>
  );
}

/** Barras horizontales para conteos (por tipo, por sucursal, por técnico…). */
export function BarList({
  items,
  label,
  total,
}: {
  items: readonly { key: string; count: number }[];
  label: string;
  /** Con él, cada barra muestra también su porcentaje del total. */
  total?: number;
}) {
  if (items.length === 0) return <p className="muted">Sin datos en el periodo.</p>;
  const max = Math.max(...items.map((i) => i.count));
  return (
    <ul className="bar-list" aria-label={label}>
      {items.map((item) => (
        <li key={item.key}>
          <span className="bar-list-label" title={item.key}>
            {item.key}
          </span>
          <span className="bar-list-track" aria-hidden="true">
            <span style={{ width: `${max ? (item.count / max) * 100 : 0}%` }} />
          </span>
          <strong className="mono">
            {item.count}
            {total ? <span className="bar-list-share"> {Math.round((item.count / total) * 100)}%</span> : null}
          </strong>
        </li>
      ))}
    </ul>
  );
}

/** Nivel de una celda de disponibilidad. */
export function availabilityLevel(percent: number | null): 'vacio' | 'pleno' | 'sano' | 'advertencia' | 'critico' {
  if (percent === null) return 'vacio';
  if (percent >= 99) return 'pleno';
  if (percent >= 90) return 'sano';
  if (percent >= 80) return 'advertencia';
  return 'critico';
}

interface HeatmapRow {
  id: number;
  name: string;
  percent: number;
  cells: readonly (number | null)[];
}

const HEATMAP_ROWS = 25;

/**
 * Mapa de calor equipo × día (o semana): de un vistazo, quién se cayó y cuándo. Los peores equipos primero; muestra
 * 25 y el resto con "Ver todos". Cada celda dice su valor al pasar el mouse.
 */
export function Heatmap({
  rows,
  buckets,
  bucketDays,
  onOpen,
}: {
  rows: readonly HeatmapRow[];
  buckets: readonly string[];
  bucketDays: number;
  onOpen: (id: number) => void;
}) {
  const [all, setAll] = useState(false);
  const visible = all ? rows : rows.slice(0, HEATMAP_ROWS);
  const every = Math.max(1, Math.ceil(buckets.length / 10));
  const bucketText = (date: string) => (bucketDays === 1 ? shortDay(date) : `sem. ${shortDay(date)}`);
  return (
    <div className="heatmap" style={{ ['--cols' as string]: buckets.length }}>
      <div className="heatmap-row heatmap-head" aria-hidden="true">
        <span />
        <span className="heatmap-cells">
          {buckets.map((b, i) => (
            <span key={b} className="heatmap-tick">
              {i % every === 0 ? bucketText(b) : ''}
            </span>
          ))}
        </span>
        <span />
      </div>
      {visible.map((row) => (
        <div key={row.id} className="heatmap-row">
          <button type="button" className="row-name mono" onClick={() => onOpen(row.id)}>
            {row.name}
          </button>
          <span className="heatmap-cells" role="img" aria-label={`${row.name}: ${row.percent}% en línea en el periodo`}>
            {row.cells.map((cell, i) => (
              <span
                key={i}
                className="heatmap-cell"
                data-level={availabilityLevel(cell)}
                title={`${row.name} · ${bucketText(buckets[i] ?? '')}: ${cell === null ? 'sin datos' : `${num(cell)}%`}`}
              />
            ))}
          </span>
          <span className="mono heatmap-total" data-level={availabilityLevel(row.percent)}>
            {num(row.percent)}%
          </span>
        </div>
      ))}
      <div className="heatmap-foot">
        <span className="heatmap-legend" aria-hidden="true">
          <span className="heatmap-cell" data-level="critico" /> &lt;80%
          <span className="heatmap-cell" data-level="advertencia" /> 80–90%
          <span className="heatmap-cell" data-level="sano" /> 90–99%
          <span className="heatmap-cell" data-level="pleno" /> ≥99%
          <span className="heatmap-cell" data-level="vacio" /> sin datos
        </span>
        {rows.length > HEATMAP_ROWS && (
          <button type="button" className="btn btn-outline btn-sm" onClick={() => setAll(!all)}>
            {all ? 'Ver solo los peores' : `Ver los ${rows.length} equipos`}
            <Icon name="chevronDown" size={14} />
          </button>
        )}
      </div>
    </div>
  );
}

/** Barra de porcentaje con su número; el nivel lo decide quien la usa. */
export function PercentBar({ value, level }: { value: number | null; level?: Tone }) {
  if (value === null) return <span className="muted">—</span>;
  return (
    <span className="report-pct">
      <span className="report-pct-track" data-level={level} aria-hidden="true">
        <span style={{ width: `${Math.min(100, Math.max(0, value))}%` }} />
      </span>
      <span className="mono">{num(value)}%</span>
    </span>
  );
}

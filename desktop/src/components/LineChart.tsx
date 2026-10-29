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
}

const WIDTH = 700;
const HEIGHT = 230;
const PAD = { top: 10, right: 8, bottom: 4, left: 36 };

/** Gráfica de líneas 0-100% con umbral. Escala fija para que dos equipos se comparen a simple vista. */
export function LineChart({ series, xLabels, threshold }: LineChartProps) {
  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;
  const y = (value: number) => PAD.top + plotH - (value / 100) * plotH;
  const grid = [0, 25, 50, 75, 100];

  const pathOf = (values: readonly number[]) =>
    values
      .map((value, i) => {
        const x = PAD.left + (i / Math.max(values.length - 1, 1)) * plotW;
        return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)} ${y(value).toFixed(1)}`;
      })
      .join(' ');

  return (
    <div className="line-chart">
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        role="img"
        aria-label={`Gráfica de ${series.map((s) => s.name).join(' y ')}`}
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
          const lastX = PAD.left + plotW;
          return (
            <g key={s.name} className={`series-${s.color}`}>
              {s.area && <path className="chart-area" d={`${line} L${lastX} ${y(0)} L${PAD.left} ${y(0)} Z`} />}
              <path className="chart-line" d={line} />
            </g>
          );
        })}
      </svg>
      <div className="line-chart-x mono" aria-hidden="true">
        {xLabels.map((label, index) => (
          <span key={index}>{label}</span>
        ))}
      </div>
    </div>
  );
}

export type MetricLevel = 'sano' | 'advertencia' | 'critico';

/** Umbrales de la maqueta: desde 80% advertencia, desde 90% crítico. */
export function metricLevel(percent: number): MetricLevel {
  if (percent >= 90) return 'critico';
  if (percent >= 80) return 'advertencia';
  return 'sano';
}

interface MiniBarProps {
  label: string;
  /** Porcentaje de uso; null cuando el equipo no reporta la métrica. */
  percent: number | null;
}

/** Porcentaje con barra delgada, usado en las columnas CPU, RAM y disco de la lista. */
export function MiniBar({ label, percent }: MiniBarProps) {
  if (percent === null) {
    return (
      <span className="mini-bar mini-bar-empty" aria-label={`${label}: sin datos`}>
        —
      </span>
    );
  }
  const level = metricLevel(percent);
  return (
    <span className="mini-bar" data-level={level}>
      <span className="mini-bar-value mono">{percent}%</span>
      <span
        className="mini-bar-track"
        role="meter"
        aria-label={label}
        aria-valuenow={percent}
        aria-valuemin={0}
        aria-valuemax={100}
      >
        <span className="mini-bar-fill" style={{ width: `${percent}%` }} />
      </span>
    </span>
  );
}

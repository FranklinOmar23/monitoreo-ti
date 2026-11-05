interface SparklineProps {
  values: readonly number[];
  /** Escala vertical; por defecto 0-100 (porcentajes). */
  min?: number;
  max?: number;
}

/** Línea mínima de tendencia. El color lo da `currentColor`. */
export function Sparkline({ values, min = 0, max = 100 }: SparklineProps) {
  const width = 100;
  const height = 28;
  const span = max - min || 1;
  const points = values.map((value, i) => {
    const x = (i / Math.max(values.length - 1, 1)) * width;
    const y = height - ((value - min) / span) * (height - 4) - 2;
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  });

  return (
    <svg className="sparkline" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" aria-hidden="true">
      <polyline
        points={points.join(' ')}
        fill="none"
        stroke="currentColor"
        strokeWidth="1.5"
        vectorEffect="non-scaling-stroke"
        pathLength={1}
      />
    </svg>
  );
}

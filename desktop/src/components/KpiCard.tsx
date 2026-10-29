import type { ReactNode } from 'react';

interface KpiCardProps {
  label: string;
  value: number;
  /** Unidad junto al número ("equipos") o porcentaje del total. */
  unit?: string;
  tone?: 'neutral' | 'sano' | 'advertencia' | 'critico' | 'desconectado';
  note?: ReactNode;
}

export function KpiCard({ label, value, unit, tone = 'neutral', note }: KpiCardProps) {
  return (
    <div className="card kpi" data-tone={tone}>
      <span className="eyebrow">
        <span className="kpi-dot" aria-hidden="true" />
        {label}
      </span>
      <span className="kpi-value">
        <strong>{value}</strong>
        {unit && <span className="kpi-unit mono">{unit}</span>}
      </span>
      {note && <span className="kpi-note">{note}</span>}
    </div>
  );
}

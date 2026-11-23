import type { ReportSummary, SummaryKpiKey } from '@monitoreoti/shared';
import { Icon } from '@components/Icon';
import { ADVANCED_KPIS, attentionText, BASIC_KPIS, findKpi, KPI_META } from './summaryLabels';
import { DeltaKpi, num, PercentBar, shortDay } from './widgets';

interface SummaryViewProps {
  data: ReportSummary;
  onOpenReport: (id: string) => void;
  onOpenEquipo: (id: number) => void;
}

/**
 * Resumen (decisión 92): la portada de Reportes. Los indicadores de cada reporte frente al periodo anterior, lo que
 * requiere atención y el reparto por sucursal; cada pieza lleva a su reporte.
 */
export function SummaryView({ data, onOpenReport, onOpenEquipo }: SummaryViewProps) {
  const tile = (key: SummaryKpiKey) => {
    const kpi = findKpi(data.kpis, key);
    if (!kpi) return null;
    const meta = KPI_META[key];
    return (
      <DeltaKpi
        key={key}
        label={meta.label}
        value={kpi.value}
        unit={meta.unit}
        previous={kpi.previous}
        better={meta.better}
        series={kpi.series}
        tone={kpi.value !== null && meta.tone ? meta.tone(kpi.value) : 'neutral'}
        note={kpi.previous === null && kpi.value !== null ? 'Foto de hoy' : undefined}
        onOpen={() => onOpenReport(meta.report)}
      />
    );
  };
  const maxAlerts = Math.max(1, ...data.branches.map((b) => b.alerts));

  return (
    <>
      <p className="report-compare muted no-print">
        Comparado con el periodo anterior: {shortDay(data.previousFrom)} – {shortDay(data.previousTo)}
      </p>
      <div className="report-kpi-grid">{BASIC_KPIS.map(tile)}</div>
      {data.advanced ? (
        <div className="report-kpi-grid">{ADVANCED_KPIS.map(tile)}</div>
      ) : (
        <div className="report-locked">
          <Icon name="lock" size={16} />
          <span>
            Rendimiento, tickets y accesos remotos: <strong>disponible en el plan Empresarial</strong>
          </span>
        </div>
      )}

      <div className="report-split">
        <section className="card report-attention" aria-label="Requiere atención">
          <header className="card-header">
            <h2>Requiere atención</h2>
            <span className="muted mono">{data.attention.length}</span>
          </header>
          {data.attention.length === 0 ? (
            <p className="report-all-good">
              <Icon name="check" size={16} /> Todo en orden: nada requiere atención en este periodo.
            </p>
          ) : (
            <ul>
              {data.attention.map((item) => (
                <li key={item.code} data-level={item.level}>
                  <span className="report-level mono">{item.level === 'critico' ? 'CRIT' : 'ADV'}</span>
                  <div>
                    <button type="button" className="report-attention-title" onClick={() => onOpenReport(item.report)}>
                      {attentionText(item.code, item.count)}
                    </button>
                    {item.machines.length > 0 && (
                      <span className="report-attention-machines">
                        {item.machines.map((m) => (
                          <button
                            key={m.id}
                            type="button"
                            className="report-machine mono"
                            onClick={() => onOpenEquipo(m.id)}
                          >
                            {m.name}
                          </button>
                        ))}
                        {item.count > item.machines.length && (
                          <span className="muted mono">+{item.count - item.machines.length}</span>
                        )}
                      </span>
                    )}
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="card" aria-label="Por sucursal">
          <header className="card-header">
            <h2>Por sucursal</h2>
          </header>
          {data.branches.length === 0 ? (
            <p className="muted">Sin equipos con estos filtros.</p>
          ) : (
            <table className="data-table data-table-compact report-branches">
              <thead>
                <tr>
                  <th>Sucursal</th>
                  <th data-numeric>Equipos</th>
                  <th>Disponibilidad</th>
                  <th>Alertas</th>
                </tr>
              </thead>
              <tbody>
                {data.branches.map((b) => (
                  <tr key={b.name}>
                    <td>{b.name}</td>
                    <td data-numeric className="mono">
                      {b.machines}
                    </td>
                    <td>
                      <PercentBar
                        value={b.availability}
                        level={
                          b.availability === null
                            ? undefined
                            : b.availability >= 95
                              ? 'sano'
                              : b.availability >= 90
                                ? 'advertencia'
                                : 'critico'
                        }
                      />
                    </td>
                    <td>
                      <span className="report-count-bar">
                        <span className="report-count-track" aria-hidden="true">
                          <span style={{ width: `${(b.alerts / maxAlerts) * 100}%` }} />
                        </span>
                        <span className="mono">{num(b.alerts)}</span>
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>
      </div>
    </>
  );
}

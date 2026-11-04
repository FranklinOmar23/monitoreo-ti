import { useQuery } from '@tanstack/react-query';
import { ErrorState, EmptyState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { KpiCard } from '@components/KpiCard';
import { PageHeader } from '@components/PageHeader';
import { StackedBars } from '@components/StackedBars';
import { useLeaving } from '@components/useLeaving';
import type { DashboardData, StatusCounts } from '@monitoreoti/shared';
import { dayLabel, timeAgo } from '../data/format';
import { usePanelApi } from '../data/PanelData';

interface DashboardPageProps {
  onOpenEquipo: (id: number) => void;
  onNavigate: (id: string) => void;
}

/** W-04 Dashboard general. */
export function DashboardPage({ onOpenEquipo, onNavigate }: DashboardPageProps) {
  const api = usePanelApi();
  const query = useQuery({ queryKey: ['dashboard'], queryFn: () => api.dashboard(), refetchInterval: 60_000 });

  return (
    <>
      <PageHeader
        title="Dashboard"
        subtitle={`Todas sus sucursales · actualizado ${query.dataUpdatedAt ? timeAgo(new Date(query.dataUpdatedAt).toISOString()) : '…'}`}
      />
      {query.isPending ? (
        <Loading />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <DashboardContent data={query.data} onOpenEquipo={onOpenEquipo} onNavigate={onNavigate} />
      )}
    </>
  );
}

const percent = (part: number, total: number) => (total > 0 ? `${((part / total) * 100).toFixed(1)}%` : '0%');

function DashboardContent({ data, onOpenEquipo, onNavigate }: { data: DashboardData } & DashboardPageProps) {
  const { counts } = data.kpis;
  const recentAlerts = useLeaving(data.recentAlerts, (alert) => alert.id);
  const max = Math.max(1, ...data.fleetHealth.map((d) => d.healthy + d.warning + d.critical + d.disconnected));

  return (
    <>
      <div className="kpi-grid">
        <KpiCard
          label="Total"
          tone="sano"
          value={counts.total}
          unit="equipos"
          note={`+${data.kpis.registeredThisWeek} registrados esta semana`}
        />
        <KpiCard
          label="En línea"
          tone="sano"
          value={counts.online}
          unit={percent(counts.online, counts.total)}
          note={`${counts.healthy} sanos`}
        />
        <KpiCard
          label="Advertencia"
          tone="advertencia"
          value={counts.warning}
          unit={percent(counts.warning, counts.total)}
        />
        <KpiCard
          label="Críticos"
          tone="critico"
          value={counts.critical}
          unit={percent(counts.critical, counts.total)}
          note={`${data.kpis.criticalWithOpenTicket} con ticket abierto`}
        />
        <KpiCard
          label="Desconectados"
          tone="desconectado"
          value={counts.disconnected}
          unit={percent(counts.disconnected, counts.total)}
          note={`${data.kpis.disconnectedOver24h} llevan más de 24 h`}
        />
      </div>

      <div className="dash-row dash-row-main">
        <Card
          title="Salud de la flota · últimos 7 días"
          aside={
            <ul className="legend">
              <li data-status="sano">Sano</li>
              <li data-status="advertencia">Advertencia</li>
              <li data-status="critico">Crítico</li>
              <li data-status="desconectado">Desconectado</li>
            </ul>
          }
        >
          <StackedBars
            max={max}
            columns={data.fleetHealth.map((day) => {
              const label = dayLabel(day.date);
              return {
                label,
                highlight: label === 'HOY',
                sano: day.healthy,
                advertencia: day.warning,
                critico: day.critical,
                desconectado: day.disconnected,
              };
            })}
          />
        </Card>

        <Card
          title="Sucursales"
          aside={
            <button type="button" className="link" onClick={() => onNavigate('sucursales')}>
              Ver todas
            </button>
          }
        >
          {data.branches.length === 0 ? (
            <EmptyState title="Sin sucursales" />
          ) : (
            <ul className="branch-list">
              {data.branches.slice(0, 6).map((branch) => (
                <li key={branch.id}>
                  <div className="branch-head">
                    <span className="branch-name">
                      <span className="dot" data-status={worst(branch.counts)} aria-hidden="true" />
                      {branch.name}
                    </span>
                    <span className="branch-meta mono">
                      {branch.counts.total} equipos
                      {branch.counts.critical > 0 && ` · ${branch.counts.critical} crit.`}
                      {branch.counts.warning > 0 && ` · ${branch.counts.warning} adv.`}
                    </span>
                  </div>
                  <StatusBar counts={branch.counts} label={`Estado de ${branch.name}`} />
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>

      <div className="dash-row dash-row-bottom">
        <Card
          title="Alertas críticas recientes"
          aside={
            <button type="button" className="link" onClick={() => onNavigate('alertas')}>
              Ir a alertas
            </button>
          }
        >
          {data.recentAlerts.length === 0 ? (
            <EmptyState title="Sin alertas abiertas">Todo en orden por ahora.</EmptyState>
          ) : (
            <ul className="alert-list">
              {recentAlerts.map(({ item: alert, leaving }) => (
                <li key={alert.id} data-leaving={leaving || undefined}>
                  <span className="alert-icon" data-status={alert.level}>
                    <Icon name={alert.level === 'critico' ? 'bolt' : 'bell'} />
                  </span>
                  <div className="alert-text">
                    <strong>{alert.message}</strong>
                    <span className="mono">
                      {alert.machineName ?? '—'}
                      {alert.branchName && ` · ${alert.branchName}`}
                    </span>
                  </div>
                  <span className="alert-time mono">{timeAgo(alert.createdAt)}</span>
                </li>
              ))}
            </ul>
          )}
        </Card>

        <Card title="Equipos con más problemas" aside={<span className="eyebrow">Top 5 · 7 días</span>}>
          {data.topProblems.length === 0 ? (
            <EmptyState title="Sin problemas en los últimos 7 días" />
          ) : (
            <ol className="problem-list">
              {data.topProblems.map((machine, index) => (
                <li key={machine.id}>
                  <span className="problem-rank mono">{String(index + 1).padStart(2, '0')}</span>
                  <button type="button" className="problem-name mono" onClick={() => onOpenEquipo(machine.id)}>
                    <span className="dot" data-status={machine.status} aria-hidden="true" />
                    {machine.name}
                  </button>
                  <span className="problem-desc">{machine.summary}</span>
                  <span className="problem-count mono">{machine.events} eventos</span>
                </li>
              ))}
            </ol>
          )}
        </Card>
      </div>
    </>
  );
}

/** Peor estado presente (para el punto de color de la sucursal). */
export function worst(counts: StatusCounts): 'critico' | 'advertencia' | 'desconectado' | 'sano' {
  if (counts.critical > 0) return 'critico';
  if (counts.warning > 0) return 'advertencia';
  if (counts.disconnected > 0 && counts.disconnected === counts.total) return 'desconectado';
  return 'sano';
}

/** Barra horizontal con la proporción de cada estado. */
export function StatusBar({ counts, label }: { counts: StatusCounts; label: string }) {
  const total = Math.max(counts.total, 1);
  const width = (value: number) => `${(value / total) * 100}%`;
  return (
    <div
      className="stack-bar"
      role="img"
      aria-label={`${label}: ${counts.healthy} sanos, ${counts.warning} en advertencia, ${counts.critical} críticos, ${counts.disconnected} desconectados`}
    >
      <span data-status="sano" style={{ width: width(counts.healthy) }} />
      <span data-status="advertencia" style={{ width: width(counts.warning) }} />
      <span data-status="critico" style={{ width: width(counts.critical) }} />
      <span data-status="desconectado" style={{ width: width(counts.disconnected) }} />
    </div>
  );
}

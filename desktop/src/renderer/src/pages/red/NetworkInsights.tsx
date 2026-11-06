import { useState } from 'react';
import { Pager } from '@components/Pager';
import { usePaged } from '../../data/usePaged';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { KpiCard } from '@components/KpiCard';
import { StatusBadge } from '@components/StatusBadge';
import { timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { useDebounced } from '../../data/useDebounced';
import { DEVICE_STATUS_LABEL, DEVICE_STATUS_TONE } from './netLabels';

interface Navigation {
  onOpenDevice: (id: number) => void;
  onOpenEquipo: (id: number) => void;
}

/** W-42 Resumen de red, con las alertas de red abiertas (W-54). */
export function NetworkSummaryView({
  onOpenDevice,
  onOpenAlerta,
}: {
  onOpenDevice: (id: number) => void;
  onOpenAlerta?: (id: number) => void;
}) {
  const api = usePanelApi();
  const summary = useQuery({ queryKey: ['red', 'resumen'], queryFn: () => api.red.summary() });
  const alerts = useQuery({ queryKey: ['alertas', 'red'], queryFn: () => api.alerts({ red: true, limit: 8 }) });

  if (summary.isPending) return <Loading variant="dashboard" />;
  if (summary.isError) return <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />;
  const s = summary.data;

  return (
    <div className="net-summary">
      <div className="kpi-grid">
        <KpiCard label="Equipos de red en línea" value={s.online} unit={`de ${s.devices}`} tone="sano" />
        <KpiCard
          label="Con problemas"
          value={s.warning + s.critical}
          tone={s.critical > 0 ? 'critico' : s.warning > 0 ? 'advertencia' : 'neutral'}
          note={s.critical > 0 ? `${s.critical} críticos` : undefined}
        />
        <KpiCard
          label="Sin responder SNMP"
          value={s.notResponding}
          tone={s.notResponding > 0 ? 'desconectado' : 'neutral'}
          note={s.withoutCollector > 0 ? `${s.withoutCollector} sin recolector asignado` : undefined}
        />
        <KpiCard
          label="Puertos en uso"
          value={s.portsConnected}
          unit={`de ${s.ports}`}
          note={s.portsWithProblems > 0 ? `${s.portsWithProblems} con errores o caídos` : `${s.trunks} troncales`}
          tone={s.portsWithProblems > 0 ? 'advertencia' : 'neutral'}
        />
      </div>
      <div className="kpi-grid">
        <KpiCard
          label="Equipos desconocidos"
          value={s.unknownDevices}
          tone={s.unknownDevices > 0 ? 'advertencia' : 'neutral'}
        />
        <KpiCard
          label="Diferencias con lo documentado"
          value={s.differences}
          tone={s.differences > 0 ? 'advertencia' : 'neutral'}
        />
        <KpiCard label="Alertas de red abiertas" value={s.openAlerts} tone={s.openAlerts > 0 ? 'critico' : 'neutral'} />
        <KpiCard
          label="Recolectores conectados"
          value={s.collectorsOnline}
          unit={`de ${s.collectors}`}
          tone={s.collectorsOnline < s.collectors ? 'desconectado' : 'neutral'}
        />
      </div>

      <div className="ficha-grid">
        <Card title="Necesitan atención" className="ficha-main">
          {s.attention.length === 0 ? (
            <EmptyState title="Toda la red responde sin problemas." />
          ) : (
            <ul className="net-attention">
              {s.attention.map((d) => (
                <li key={d.id}>
                  <StatusBadge status={DEVICE_STATUS_TONE[d.status]} label={DEVICE_STATUS_LABEL[d.status]} />
                  <button type="button" className="row-name mono" onClick={() => onOpenDevice(d.id)}>
                    {d.name}
                  </button>
                  <span className="muted">
                    {d.branchName}
                    {d.rackName && ` · ${d.rackName}`}
                    {d.portsWithProblems > 0 && ` · ${d.portsWithProblems} puertos con problemas`}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>
        <Card title="Alertas de red" className="ficha-side">
          {alerts.isPending ? (
            <Loading variant="table" columns={5} />
          ) : alerts.isError ? (
            <ErrorState error={alerts.error} onRetry={() => void alerts.refetch()} />
          ) : alerts.data.data.data.length === 0 ? (
            <EmptyState title="Sin alertas de red abiertas." />
          ) : (
            <ul className="net-alerts">
              {alerts.data.data.data.map((a) => (
                <li key={a.id} data-level={a.level}>
                  <button type="button" className="link-quiet" onClick={() => onOpenAlerta?.(a.id)}>
                    {a.message}
                  </button>
                  <span className="muted">
                    {a.deviceName} · {timeAgo(a.createdAt)}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
    </div>
  );
}

/** W-53 Equipos desconocidos: MAC vistas en puertos de acceso sin equipo registrado. */
export function UnknownList({ onOpenDevice }: Pick<Navigation, 'onOpenDevice'>) {
  const api = usePanelApi();
  const { query: list, data: page, items, setPage } = usePaged(['red', 'desconocidos'], (p) => api.red.unknown(p));

  if (list.isPending) return <Loading variant="table" columns={5} />;
  if (list.isError) return <ErrorState error={list.error} onRetry={() => void list.refetch()} />;
  if (items.length === 0)
    return (
      <EmptyState title="Sin equipos desconocidos">
        Todo lo que ven los switches en sus puertos de acceso es un equipo registrado.
      </EmptyState>
    );

  return (
    <div className="table-card">
      <table className="data-table">
        <thead>
          <tr>
            <th>MAC</th>
            <th>Fabricante</th>
            <th>IP</th>
            <th>Dónde</th>
            <th>VLAN</th>
            <th>Visto</th>
          </tr>
        </thead>
        <tbody>
          {items.map((u) => (
            <tr key={`${u.portId}-${u.mac}`}>
              <td className="mono">
                {u.isRandom ? (
                  <>
                    {u.count} {u.count === 1 ? 'MAC aleatoria' : 'MAC aleatorias'}
                    <span className="row-sub">Celulares u otros equipos que cambian de MAC</span>
                  </>
                ) : (
                  u.mac
                )}
              </td>
              <td>{u.vendor ?? '—'}</td>
              <td className="mono">{u.ip ?? '—'}</td>
              <td>
                <button type="button" className="row-name mono" onClick={() => onOpenDevice(u.deviceId)}>
                  {u.portLabel}
                </button>
                {u.viaAccessPoint && <span className="row-sub">Vía access point (Wi-Fi)</span>}
              </td>
              <td className="mono">{u.vlan ?? '—'}</td>
              <td>
                {timeAgo(u.lastSeenAt)}
                <span className="row-sub">desde {timeAgo(u.firstSeenAt)}</span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <Pager data={page} noun="equipos desconocidos" onPage={setPage} />
    </div>
  );
}

const DIFFERENCE_ICON = { enlace_sin_cable: 'plug', cable_distinto: 'network', equipo_sin_cable: 'monitor' } as const;

/** W-66 Diferencias entre lo detectado por SNMP y lo documentado. */
export function DifferencesList({ onOpenDevice, onOpenEquipo }: Navigation) {
  const api = usePanelApi();
  const { query: list, data: page, items, setPage } = usePaged(['red', 'diferencias'], (p) => api.red.differences(p));

  if (list.isPending) return <Loading variant="table" columns={5} />;
  if (list.isError) return <ErrorState error={list.error} onRetry={() => void list.refetch()} />;
  if (items.length === 0)
    return (
      <EmptyState title="Sin diferencias">
        Lo que detectan los switches coincide con el cableado documentado.
      </EmptyState>
    );

  return (
    <>
    <ul className="difference-list">
      {items.map((d, i) => (
        <li key={i} className="card">
          <Icon name={DIFFERENCE_ICON[d.kind]} />
          <div>
            <strong>{d.title}</strong>
            <p>{d.detail}</p>
            <div className="row-actions">
              {d.deviceId !== null && (
                <button type="button" className="btn btn-outline" onClick={() => onOpenDevice(d.deviceId!)}>
                  Ver dispositivo
                </button>
              )}
              {d.machineId !== null && (
                <button type="button" className="btn btn-outline" onClick={() => onOpenEquipo(d.machineId!)}>
                  Ver equipo
                </button>
              )}
            </div>
          </div>
        </li>
      ))}
    </ul>
    <Pager data={page} noun="diferencias" onPage={setPage} />
    </>
  );
}

/** W-52 Buscar una MAC, una IP o un equipo: dónde está conectado. */
export function NetworkSearch({ onOpenDevice, onOpenEquipo }: Navigation) {
  const api = usePanelApi();
  const [query, setQuery] = useState('');
  const q = useDebounced(query.trim());
  const results = useQuery({
    queryKey: ['red', 'buscar', q],
    queryFn: () => api.red.search(q),
    enabled: q.length >= 2,
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="MAC (AA:BB:CC…), IP o nombre del equipo"
            aria-label="Buscar MAC o IP"
            onChange={(e) => setQuery(e.target.value)}
            autoFocus
          />
        </label>
      </div>
      {q.length < 2 ? (
        <EmptyState title="Escriba al menos dos caracteres de una MAC, una IP o un nombre." />
      ) : results.isPending ? (
        <Loading variant="table" columns={5} />
      ) : results.isError ? (
        <ErrorState error={results.error} onRetry={() => void results.refetch()} />
      ) : results.data.length === 0 ? (
        <EmptyState title="Ningún switch ha visto esa MAC o IP." />
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>MAC</th>
                <th>IP</th>
                <th>Equipo</th>
                <th>Conectado en</th>
                <th>VLAN</th>
                <th>Visto</th>
              </tr>
            </thead>
            <tbody>
              {results.data.map((r) => (
                <tr key={`${r.mac}-${r.portId ?? 'x'}`}>
                  <td className="mono">
                    {r.mac}
                    {r.vendor && <span className="row-sub">{r.vendor}</span>}
                  </td>
                  <td className="mono">{r.ip ?? '—'}</td>
                  <td>
                    {r.machineId !== null ? (
                      <button type="button" className="row-name mono" onClick={() => onOpenEquipo(r.machineId!)}>
                        {r.machineName}
                      </button>
                    ) : (
                      <span className="muted">Desconocido</span>
                    )}
                  </td>
                  <td>
                    {r.deviceId !== null ? (
                      <button type="button" className="row-name mono" onClick={() => onOpenDevice(r.deviceId!)}>
                        {r.portLabel}
                      </button>
                    ) : (
                      <span className="muted">Ningún switch lo ha visto</span>
                    )}
                    {r.viaAccessPoint && <span className="row-sub">Vía access point (Wi-Fi)</span>}
                  </td>
                  <td className="mono">{r.vlan ?? '—'}</td>
                  <td>{r.lastSeenAt ? timeAgo(r.lastSeenAt) : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

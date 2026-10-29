import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { MachineDetail } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { dateTime, megabytes, timeAgo, typeLabel, usedOfTotal } from '../data/format';
import { usePanelApi } from '../data/PanelData';

interface FichaTabsProps {
  tab: string;
  machine: MachineDetail;
}

/** Pestañas W-07 a W-12 de la ficha (la de Resumen está en FichaPage). */
export function FichaTabs({ tab, machine }: FichaTabsProps) {
  switch (tab) {
    case 'componentes':
      return <ComponentsTab machine={machine} />;
    case 'errores':
      return <ErrorsTab machineId={machine.id} />;
    case 'procesos':
      return <ProcessesTab machine={machine} />;
    case 'software':
      return <SoftwareTab machineId={machine.id} />;
    case 'red':
      return <NetworkTab machineId={machine.id} />;
    case 'historial':
      return <HistoryTab machineId={machine.id} />;
    case 'qr':
      return <InfoTab machine={machine} />;
    default:
      return null;
  }
}

/** Consulta de una pestaña con sus estados de carga, error y vacío. */
function useTab<T>(key: string, machineId: number, fetcher: () => Promise<T[]>) {
  return useQuery({ queryKey: [key, machineId], queryFn: fetcher });
}

function TabState({
  query,
  empty,
  children,
}: {
  query: ReturnType<typeof useTab<unknown>>;
  empty: string;
  children: () => React.ReactNode;
}) {
  if (query.isPending) return <Loading />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  if (query.data.length === 0) return <EmptyState title={empty} />;
  return <>{children()}</>;
}

const HEALTH = { sano: 'Sano', advertencia: 'Revisar', critico: 'Reemplazar', desconocido: 'Sin datos' } as const;

function ComponentsTab({ machine }: { machine: MachineDetail }) {
  if (machine.components.length === 0)
    return <EmptyState title="Sin inventario de hardware">El agente lo envía al iniciar y cada 24 horas.</EmptyState>;

  return (
    <>
      <div className="table-card">
        <table className="data-table">
          <thead>
            <tr>
              <th>Tipo</th>
              <th>Componente</th>
              <th>Capacidad</th>
              <th>Salud</th>
              <th>Detalle</th>
              <th>Actualizado</th>
            </tr>
          </thead>
          <tbody>
            {machine.components.map((c) => (
              <tr key={c.id}>
                <td className="mono">{c.type}</td>
                <td>{c.description ?? '—'}</td>
                <td>{c.capacity ?? '—'}</td>
                <td>
                  <span className="component-state" data-status={c.health === 'desconocido' ? undefined : c.health}>
                    {HEALTH[c.health]}
                  </span>
                </td>
                <td className="muted">{c.detail ?? '—'}</td>
                <td className="mono">{timeAgo(c.updatedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {machine.volumes.length > 0 && (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Unidad</th>
                <th>Etiqueta</th>
                <th>Usado</th>
                <th>Libre</th>
                <th>Actualizado</th>
              </tr>
            </thead>
            <tbody>
              {machine.volumes.map((v) => (
                <tr key={v.drive}>
                  <td className="mono">{v.drive}</td>
                  <td>{v.label ?? '—'}</td>
                  <td className="mono">{usedOfTotal(v.totalGb - v.freeGb, v.totalGb)}</td>
                  <td className="mono">{v.totalGb > 0 ? `${Math.round((100 * v.freeGb) / v.totalGb)} %` : '—'}</td>
                  <td className="mono">{timeAgo(v.updatedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

const LEVEL_STATUS = { info: undefined, advertencia: 'advertencia', error: 'critico', critico: 'critico' } as const;

function ErrorsTab({ machineId }: { machineId: number }) {
  const api = usePanelApi();
  const query = useTab('eventos', machineId, () => api.events(machineId));
  return (
    <TabState query={query} empty="Sin errores del sistema registrados">
      {() => (
        <ul className="event-list">
          {query.data!.map((event) => (
            <li key={event.id} className="card">
              <span className="dot" data-status={LEVEL_STATUS[event.level]} aria-hidden="true" />
              <div>
                <strong>{event.plainMessage ?? event.message ?? 'Error del sistema'}</strong>
                <span className="mono muted">
                  {event.source ?? '—'}
                  {event.code !== null && ` ${event.code}`} · {dateTime(event.at)}
                </span>
                {event.plainMessage && event.message && <p className="event-raw mono">{event.message}</p>}
              </div>
            </li>
          ))}
        </ul>
      )}
    </TabState>
  );
}

function ProcessesTab({ machine }: { machine: MachineDetail }) {
  const api = usePanelApi();
  const query = useTab('procesos', machine.id, () => api.processes(machine.id));
  return (
    <div className="ficha-grid">
      <Card
        title={`Procesos con más consumo${machine.processCount ? ` · ${machine.processCount} en ejecución` : ''}`}
        aside={query.data?.[0] ? <span className="eyebrow">{timeAgo(query.data[0].at)}</span> : undefined}
      >
        <TabState query={query} empty="Sin datos de procesos todavía">
          {() => (
            <table className="data-table data-table-compact">
              <thead>
                <tr>
                  <th>Proceso</th>
                  <th>CPU</th>
                  <th>Memoria</th>
                  <th>PID</th>
                </tr>
              </thead>
              <tbody>
                {query.data!.map((p, i) => (
                  <tr key={`${p.pid}-${i}`}>
                    <td className="mono">{p.name}</td>
                    <td className="mono">{p.cpu === null ? '—' : `${p.cpu.toFixed(1)}%`}</td>
                    <td className="mono">{megabytes(p.ramMb)}</td>
                    <td className="mono">{p.pid ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </TabState>
      </Card>
      <ServicesCard machineId={machine.id} />
    </div>
  );
}

const SERVICE_STATE = { en_ejecucion: 'En ejecución', detenido: 'Detenido', otro: 'Cambiando' } as const;
const START_MODE = {
  automatico: 'Automático',
  automatico_retrasado: 'Automático (retrasado)',
  manual: 'Manual',
  deshabilitado: 'Deshabilitado',
} as const;

/** Servicios automáticos; los que se cayeron (estaban en ejecución y se detuvieron) van primero y en rojo. */
function ServicesCard({ machineId }: { machineId: number }) {
  const api = usePanelApi();
  const query = useTab('servicios', machineId, () => api.services(machineId));
  const fallen = query.data?.filter((s) => s.stoppedSince).length ?? 0;
  return (
    <Card
      title="Servicios automáticos"
      aside={fallen > 0 ? <span className="eyebrow eyebrow-critico">{fallen} caídos</span> : undefined}
    >
      <TabState query={query} empty="Sin datos de servicios todavía">
        {() => (
          <table className="data-table data-table-compact">
            <thead>
              <tr>
                <th>Servicio</th>
                <th>Estado</th>
                <th>Inicio</th>
              </tr>
            </thead>
            <tbody>
              {query.data!.map((s) => (
                <tr key={s.name}>
                  <td>
                    {s.displayName ?? s.name}
                    <span className="mono muted service-name">{s.name}</span>
                  </td>
                  <td>
                    <span
                      className="component-state"
                      data-status={s.stoppedSince ? 'critico' : s.state === 'en_ejecucion' ? 'sano' : undefined}
                      title={s.stoppedSince ? `Detenido ${timeAgo(s.stoppedSince)}` : undefined}
                    >
                      {SERVICE_STATE[s.state]}
                    </span>
                  </td>
                  <td className="muted">{START_MODE[s.startMode]}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </TabState>
    </Card>
  );
}

function SoftwareTab({ machineId }: { machineId: number }) {
  const api = usePanelApi();
  const query = useTab('software', machineId, () => api.software(machineId));
  const [filter, setFilter] = useState('');
  return (
    <TabState query={query} empty="Sin inventario de software">
      {() => {
        const needle = filter.trim().toLowerCase();
        const items = query.data!.filter(
          (s) => !needle || s.name.toLowerCase().includes(needle) || (s.publisher ?? '').toLowerCase().includes(needle),
        );
        return (
          <>
            <div className="filters">
              <label className="search filter-search">
                <input
                  type="search"
                  value={filter}
                  placeholder={`Buscar entre ${query.data!.length} programas`}
                  aria-label="Buscar programa"
                  onChange={(e) => setFilter(e.target.value)}
                />
              </label>
            </div>
            <div className="table-card">
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Programa</th>
                    <th>Versión</th>
                    <th>Editor</th>
                    <th>Instalado</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((s, i) => (
                    <tr key={`${s.name}-${s.version}-${i}`}>
                      <td>{s.name}</td>
                      <td className="mono">{s.version ?? '—'}</td>
                      <td>{s.publisher ?? '—'}</td>
                      <td className="mono">{s.installedOn ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        );
      }}
    </TabState>
  );
}

const INTERFACE_TYPES = { cableada: 'Cableada', wifi: 'Wi-Fi', virtual: 'Virtual', otra: 'Otra' } as const;

function NetworkTab({ machineId }: { machineId: number }) {
  const api = usePanelApi();
  const query = useTab('interfaces', machineId, () => api.interfaces(machineId));
  return (
    <TabState query={query} empty="Sin tarjetas de red reportadas">
      {() => (
        <>
          <div className="table-card">
            <table className="data-table">
              <thead>
                <tr>
                  <th>Tarjeta</th>
                  <th>Tipo</th>
                  <th>MAC</th>
                  <th>IP</th>
                  <th>Velocidad</th>
                </tr>
              </thead>
              <tbody>
                {query.data!.map((nic) => (
                  <tr key={nic.macAddress}>
                    <td>{nic.name}</td>
                    <td>{INTERFACE_TYPES[nic.type]}</td>
                    <td className="mono">{nic.macAddress}</td>
                    <td className="mono">{nic.ipAddress ?? '—'}</td>
                    <td className="mono">
                      {nic.speedMbps === null
                        ? '—'
                        : nic.speedMbps >= 1000
                          ? `${nic.speedMbps / 1000} Gbps`
                          : `${nic.speedMbps} Mbps`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted note">
            La conexión al switch (puerto y VLAN) se muestra cuando esté el módulo de red (Fase 4).
          </p>
        </>
      )}
    </TabState>
  );
}

const ACTIONS: Record<string, string> = {
  'agente.registro': 'Agente registrado',
  'agente.reinstalacion': 'Agente reinstalado',
  'maquina.crear': 'Equipo creado',
  'maquina.editar': 'Equipo editado',
  'maquina.baja': 'Equipo dado de baja',
  'agente.aprobado': 'Agente aprobado',
  'agente.rechazado': 'Agente rechazado',
};

function HistoryTab({ machineId }: { machineId: number }) {
  const api = usePanelApi();
  const query = useTab('historial', machineId, () => api.history(machineId));
  return (
    <TabState query={query} empty="Sin cambios registrados">
      {() => (
        <ul className="timeline">
          {query.data!.map((item) => (
            <li key={item.id}>
              <strong>{ACTIONS[item.action] ?? item.action}</strong>
              <span className="muted">
                {item.userName ?? 'Sistema'} · {dateTime(item.at)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </TabState>
  );
}

function InfoTab({ machine }: { machine: MachineDetail }) {
  const api = usePanelApi();
  const qr = useQuery({ queryKey: ['qr', machine.id], queryFn: () => api.qr(machine.id) });
  const rows: [string, string | null][] = [
    ['Tipo', typeLabel(machine.type)],
    ['Marca', machine.brand],
    ['Modelo', machine.model],
    ['Serie', machine.serialNumber],
    ['Sistema operativo', machine.operatingSystem],
    ['MAC principal', machine.macAddress],
    ['IP', machine.ipAddress],
    ['Sucursal', machine.branchName],
    ['Departamento', machine.departmentName],
    ['Responsable', machine.assignedTo],
    ['Fecha de compra', machine.purchaseDate],
    ['Garantía hasta', machine.warrantyUntil],
    ['Versión del agente', machine.agentVersion ?? (machine.hasAgent ? null : 'Sin agente')],
    ['Registrado', dateTime(machine.createdAt)],
  ];

  return (
    <div className="ficha-grid">
      <Card title="Información del equipo">
        <dl className="info-list">
          {rows.map(([label, value]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd className={label === 'MAC principal' || label === 'IP' || label === 'Serie' ? 'mono' : undefined}>
                {value ?? '—'}
              </dd>
            </div>
          ))}
        </dl>
      </Card>
      <Card title="Etiqueta QR">
        {qr.isPending ? (
          <Loading />
        ) : qr.isError ? (
          <ErrorState error={qr.error} />
        ) : (
          <div className="qr-label">
            <img src={qr.data.png} alt={`Código QR de ${machine.name}`} width={200} height={200} />
            <strong className="mono">{machine.name}</strong>
            <span className="muted">{machine.branchName}</span>
            <span className="mono qr-code-text">{qr.data.codigo}</span>
          </div>
        )}
      </Card>
    </div>
  );
}

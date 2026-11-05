import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { MachineDetail, MetricPoint, MetricRange, NetworkInterfaceItem, SessionType } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon, type IconName } from '@components/Icon';
import { LineChart } from '@components/LineChart';
import { metricLevel } from '@components/MiniBar';
import { Sparkline } from '@components/Sparkline';
import { StatusBadge } from '@components/StatusBadge';
import { Tabs, type TabItem } from '@components/Tabs';
import { clock, dateTime, megabytes, timeAgo, typeLabel, usedOfTotal, whole, shortDateTime } from '../data/format';
import { usePanelApi, useRealtime } from '../data/PanelData';
import { FichaTabs } from './FichaTabs';
import { serviceFromAlertCode, StartServiceButton } from './remoto/StartServiceButton';

const RANGES: { value: MetricRange; label: string; title: string }[] = [
  { value: '1h', label: '1H', title: 'última hora' },
  { value: '24h', label: '24H', title: 'últimas 24 h' },
  { value: '7d', label: '7D', title: 'últimos 7 días' },
];

const STATUS_TITLE: Partial<Record<MachineDetail['status'], string>> = { critico: 'Falla crítica' };

const COMPONENT_ICONS: Record<string, IconName> = {
  cpu: 'cpu',
  ram: 'memory',
  disco: 'disk',
  gpu: 'monitor',
  bateria: 'bolt',
  placa_base: 'cpu',
  bios: 'gear',
  ventilador: 'fan',
  antivirus: 'shield',
  firewall: 'shield',
  red: 'network',
  toner: 'printer',
};

const COMPONENT_NAMES: Record<string, string> = {
  cpu: 'Procesador',
  ram: 'Memoria RAM',
  disco: 'Disco',
  gpu: 'Tarjeta de video',
  bateria: 'Batería',
  placa_base: 'Placa base',
  bios: 'BIOS',
  ventilador: 'Ventilador',
  antivirus: 'Antivirus',
  firewall: 'Firewall',
  toner: 'Consumible',
};

const HEALTH_LABEL = { sano: 'Sano', advertencia: 'Revisar', critico: 'Reemplazar', desconocido: 'Sin datos' } as const;
/** Un consumible no se "revisa": se pide o se cambia. */
const SUPPLY_HEALTH_LABEL = {
  sano: 'Bien',
  advertencia: 'Por acabarse',
  critico: 'Cambiar',
  desconocido: 'Sin nivel',
} as const;
/** Antivirus y firewall no se reemplazan: se activan. */
const SOFTWARE_HEALTH_LABEL = { ...HEALTH_LABEL, critico: 'Inactivo' } as const;
const HEALTH_ORDER = { critico: 0, advertencia: 1, sano: 2, desconocido: 3 } as const;

type Health = keyof typeof HEALTH_LABEL;

interface HealthRow {
  key: string;
  type: string;
  name: string;
  caption: string | null;
  health: Health;
  /** Texto del estado cuando no es una salud (ej. "Solo inventario" en la tarjeta de video). */
  stateLabel?: string;
}

interface MetricCardProps {
  label: string;
  icon?: IconName;
  value: number | null;
  unit: string;
  caption?: string | null;
  series: readonly number[];
  levelOf?: (value: number) => 'sano' | 'advertencia' | 'critico';
}

function MetricCard({ label, icon, value, unit, caption, series, levelOf = metricLevel }: MetricCardProps) {
  return (
    <div className="card metric-card" data-level={value === null ? 'sano' : levelOf(value)}>
      <div className="metric-head">
        <span className="eyebrow">
          {icon && <Icon name={icon} size={14} />}
          {label}
        </span>
        <span className="live">En vivo</span>
      </div>
      <div className="metric-value">
        <strong>{value === null ? '—' : `${whole(value)}${unit}`}</strong>
        {caption && <span className="metric-caption mono">{caption}</span>}
      </div>
      <Sparkline values={series} />
    </div>
  );
}

const temperatureLevel = (celsius: number) => (celsius > 90 ? 'critico' : celsius > 80 ? 'advertencia' : 'sano');

interface FichaPageProps {
  machineId: number;
  onBack: () => void;
  onEdit?: (machine: MachineDetail) => void;
  onRetire?: (machine: MachineDetail) => void;
  /** Solo si el usuario puede crear tickets. */
  onCreateTicket?: (machine: MachineDetail) => void;
  /** Solo si el usuario gestiona tickets (y con ellos, el calendario de mantenimiento). */
  onScheduleMaintenance?: (machine: MachineDetail) => void;
  /** Solo con scripts.ejecutar. */
  onRunScript?: (machine: MachineDetail) => void;
  /** Sesiones remotas que el usuario puede abrir (según sus permisos). */
  remoteTypes?: readonly SessionType[];
  onOpenSession?: (machine: MachineDetail, type: SessionType) => void;
}

/** W-06 a W-12 Ficha del equipo. */
export function FichaPage({
  machineId,
  onBack,
  onEdit,
  onRetire,
  onCreateTicket,
  onScheduleMaintenance,
  onRunScript,
  remoteTypes = [],
  onOpenSession,
}: FichaPageProps) {
  const api = usePanelApi();
  const realtime = useRealtime();
  const queries = useQueryClient();
  const [tab, setTab] = useState('resumen');
  const [range, setRange] = useState<MetricRange>('24h');

  const detail = useQuery({ queryKey: ['maquina', machineId], queryFn: () => api.machine(machineId) });
  const lastHour = useQuery({ queryKey: ['metricas', machineId, '1h'], queryFn: () => api.metrics(machineId, '1h') });
  const chart = useQuery({ queryKey: ['metricas', machineId, range], queryFn: () => api.metrics(machineId, range) });
  const processes = useQuery({ queryKey: ['procesos', machineId], queryFn: () => api.processes(machineId) });
  const interfaces = useQuery({ queryKey: ['interfaces', machineId], queryFn: () => api.interfaces(machineId) });

  // Métricas en vivo: se agregan a la última hora (tarjetas) y, en 1H, a la gráfica, sin volver a consultar.
  useEffect(() => {
    if (!realtime) return;
    return realtime.watchMachine(machineId, (point: MetricPoint) => {
      const append = (old: MetricPoint[] | undefined) => (old ? [...old, point].slice(-240) : [point]);
      queries.setQueryData<MetricPoint[]>(['metricas', machineId, '1h'], append);
      queries.setQueryData<MachineDetail>(['maquina', machineId], (old) =>
        old ? { ...old, metrics: { ...point }, lastReportAt: point.at } : old,
      );
    });
  }, [realtime, machineId, queries]);

  if (detail.isPending) return <Loading variant="detail" />;
  if (detail.isError)
    return (
      <>
        <Breadcrumb onBack={onBack} />
        <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />
      </>
    );

  const machine = detail.data;
  const recent = lastHour.data ?? [];
  const series = (pick: (p: MetricPoint) => number | null) => recent.map(pick).filter((v): v is number => v !== null);
  const chartPoints = range === '1h' ? recent : (chart.data ?? []);
  const metrics = machine.metrics;
  const disconnected = machine.status === 'desconectado';

  const ram = machine.components.find((c) => c.type === 'ram');
  const cpu = machine.components.find((c) => c.type === 'cpu');
  // El disco de las métricas es el del sistema (C:); si no hay C:, el primer volumen.
  const volume = machine.volumes.find((v) => v.drive.toUpperCase() === 'C:') ?? machine.volumes[0];
  const ramTotalGb = machine.ramTotalMb ? machine.ramTotalMb / 1024 : null;
  const ramCaption =
    ramTotalGb && metrics?.ram != null ? usedOfTotal((ramTotalGb * metrics.ram) / 100, ramTotalGb) : ram?.capacity;
  const diskCaption = volume ? usedOfTotal(volume.totalGb - volume.freeGb, volume.totalGb) : null;
  const healthRows = componentHealth(machine, interfaces.data ?? []);
  const topProcesses = (processes.data ?? []).slice(0, 5);

  const tabs: TabItem[] = [
    { id: 'resumen', label: 'Resumen' },
    { id: 'componentes', label: 'Componentes' },
    { id: 'errores', label: 'Errores', count: machine.openErrorCount },
    { id: 'procesos', label: 'Procesos y servicios' },
    { id: 'software', label: 'Software instalado' },
    { id: 'red', label: 'Red' },
    { id: 'historial', label: 'Historial' },
    { id: 'qr', label: 'Información y QR' },
  ];

  return (
    <>
      <Breadcrumb onBack={onBack} branch={machine.branchName} name={machine.name} />

      <div className="ficha-head">
        <span className="status-orb" data-status={machine.status} aria-hidden="true">
          <span />
        </span>
        <div className="ficha-id">
          <div className="ficha-title">
            <h1 className="mono">{machine.name}</h1>
            <StatusBadge status={machine.status} label={STATUS_TITLE[machine.status]} />
          </div>
          <ul className="ficha-meta">
            <li>
              <Icon name="pin" size={14} />
              {machine.branchName}
              {machine.departmentName && ` · ${machine.departmentName}`}
            </li>
            {machine.assignedTo && (
              <li>
                <Icon name="user" size={14} />
                Responsable: {machine.assignedTo}
              </li>
            )}
            <li className="mono">{machine.ipAddress ?? 'Sin IP'}</li>
            <li>
              {[machine.brand, machine.model].filter(Boolean).join(' ') || typeLabel(machine.type)}
              {machine.operatingSystem && ` · ${machine.operatingSystem}`}
            </li>
            <li>
              <Icon name="clock" size={14} />
              Último reporte {timeAgo(machine.lastReportAt)}
            </li>
          </ul>
        </div>
        <div className="ficha-actions">
          {(() => {
            const usable =
              !!onOpenSession && remoteTypes.includes('escritorio') && machine.hasAgent && machine.status !== 'baja';
            return (
              <button
                type="button"
                className="btn btn-primary"
                disabled={!usable}
                title={
                  usable
                    ? 'Ver y controlar la pantalla'
                    : machine.hasAgent
                      ? 'Sin permiso'
                      : 'El equipo no tiene agente'
                }
                onClick={() => onOpenSession?.(machine, 'escritorio')}
              >
                Conectar
              </button>
            );
          })()}
          {(['terminal', 'archivos'] as const).map((type) => {
            const usable =
              !!onOpenSession && remoteTypes.includes(type) && machine.hasAgent && machine.status !== 'baja';
            return (
              <button
                key={type}
                type="button"
                className="btn btn-outline"
                disabled={!usable}
                title={usable ? undefined : machine.hasAgent ? 'Sin permiso' : 'El equipo no tiene agente'}
                onClick={() => onOpenSession?.(machine, type)}
              >
                {type === 'terminal' ? 'Terminal' : 'Archivos'}
              </button>
            );
          })}
          {onRunScript && machine.hasAgent && machine.status !== 'baja' && (
            <button type="button" className="btn btn-outline" onClick={() => onRunScript(machine)}>
              <Icon name="terminal" size={14} />
              Ejecutar script
            </button>
          )}
          <MoreActions
            items={[
              ...(onEdit ? [{ label: 'Editar', onSelect: () => onEdit(machine) }] : []),
              ...(onScheduleMaintenance && machine.status !== 'baja'
                ? [{ label: 'Programar mantenimiento', onSelect: () => onScheduleMaintenance(machine) }]
                : []),
              ...(onRetire && machine.status !== 'baja'
                ? [{ label: 'Dar de baja', onSelect: () => onRetire(machine), danger: true }]
                : []),
            ]}
          />
          {onCreateTicket && (
            <button type="button" className="btn btn-ticket" onClick={() => onCreateTicket(machine)}>
              <Icon name="ticket" size={14} />
              Crear ticket
            </button>
          )}
        </div>
      </div>

      <Tabs items={tabs} activeId={tab} onChange={setTab} label="Secciones del equipo" />

      {tab !== 'resumen' ? (
        <FichaTabs tab={tab} machine={machine} />
      ) : (
        <div className="ficha-grid">
          <div className="ficha-main">
            {disconnected || !metrics ? (
              <Card>
                <EmptyState title={disconnected ? 'El equipo está desconectado' : 'Aún no hay métricas'}>
                  {disconnected
                    ? `No hay métricas en vivo. Último reporte ${timeAgo(machine.lastReportAt)} (${dateTime(machine.lastReportAt)}).`
                    : machine.hasAgent
                      ? 'El agente enviará la primera muestra en menos de un minuto.'
                      : 'Este equipo no tiene agente instalado.'}
                </EmptyState>
              </Card>
            ) : (
              <div className="metric-grid">
                <MetricCard
                  label="CPU"
                  icon="cpu"
                  value={metrics.cpu}
                  unit="%"
                  caption={cpu?.description}
                  series={series((p) => p.cpu)}
                />
                <MetricCard
                  label="RAM"
                  icon="memory"
                  value={metrics.ram}
                  unit="%"
                  caption={ramCaption}
                  series={series((p) => p.ram)}
                />
                <MetricCard
                  label={volume ? `Disco ${volume.drive}` : 'Disco'}
                  icon="disk"
                  value={metrics.disk}
                  unit="%"
                  caption={diskCaption}
                  series={series((p) => p.disk)}
                />
                <MetricCard
                  label="Temperatura"
                  icon="thermometer"
                  value={metrics.temperature}
                  unit="°C"
                  caption={metrics.temperature === null ? 'Sin sensor' : 'CPU paquete'}
                  series={series((p) => p.temperature)}
                  levelOf={temperatureLevel}
                />
              </div>
            )}

            <Card
              title={`CPU y RAM · ${RANGES.find((r) => r.value === range)!.title}`}
              aside={
                <div className="chart-controls">
                  <ul className="legend legend-lines">
                    <li data-series="cpu">CPU</li>
                    <li data-series="ram">RAM</li>
                    <li data-series="umbral">Umbral 90%</li>
                  </ul>
                  <div className="segmented" role="group" aria-label="Rango de tiempo">
                    {RANGES.map((r) => (
                      <button
                        key={r.value}
                        type="button"
                        aria-pressed={r.value === range}
                        onClick={() => setRange(r.value)}
                      >
                        {r.label}
                      </button>
                    ))}
                  </div>
                </div>
              }
            >
              {chartPoints.length < 2 ? (
                <EmptyState title="Sin datos en este rango" />
              ) : (
                <LineChart
                  threshold={90}
                  xLabels={axisLabels(chartPoints, range)}
                  pointLabels={chartPoints.map((p) => (range === '7d' ? shortDateTime(p.at) : clock(p.at)))}
                  series={[
                    { name: 'CPU', color: 'cpu', values: chartPoints.map((p) => p.cpu ?? 0), area: true },
                    { name: 'RAM', color: 'ram', values: chartPoints.map((p) => p.ram ?? 0) },
                  ]}
                />
              )}
            </Card>

            <Card
              title="Procesos con más consumo"
              aside={
                topProcesses.length > 0 ? (
                  <button type="button" className="link-quiet" onClick={() => setTab('procesos')}>
                    Ver todos{machine.processCount ? ` (${machine.processCount})` : ''}
                  </button>
                ) : undefined
              }
            >
              {topProcesses.length === 0 ? (
                <p className="muted">
                  {processes.isPending ? 'Cargando…' : 'El agente envía los procesos cada 5 minutos.'}
                </p>
              ) : (
                <table className="data-table data-table-compact">
                  <thead>
                    <tr>
                      <th>Proceso</th>
                      <th>CPU</th>
                      <th>Memoria</th>
                    </tr>
                  </thead>
                  <tbody>
                    {topProcesses.map((p, i) => (
                      <tr key={`${p.pid}-${i}`}>
                        <td className="mono">{p.name}</td>
                        <td className="mono">{p.cpu === null ? '—' : `${p.cpu.toFixed(1)}%`}</td>
                        <td className="mono">{megabytes(p.ramMb)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </Card>
          </div>

          <div className="ficha-side">
            <Card
              title="Problemas detectados"
              aside={
                machine.problems.length > 0 ? (
                  <span className="eyebrow eyebrow-critico">{machine.problems.length} activos</span>
                ) : undefined
              }
            >
              {machine.problems.length === 0 ? (
                <p className="muted">Sin problemas activos.</p>
              ) : (
                <ul className="problem-cards">
                  {machine.problems.map((problem) => (
                    <li key={problem.id} className="problem-card">
                      <span className="problem-card-icon" data-status={problem.level}>
                        <Icon name={problem.level === 'critico' ? 'bolt' : 'bell'} />
                      </span>
                      <div>
                        <strong>{problem.message}</strong>
                        {problem.detail && <p>{problem.detail}</p>}
                        {problem.advice && (
                          <div className="what-to-do">
                            <Icon name="wrench" size={14} />
                            <span>
                              <strong>Qué hacer:</strong> {problem.advice}
                            </span>
                          </div>
                        )}
                        <span className="problem-origin mono">
                          {problem.source ?? (problem.level === 'critico' ? 'Crítico' : 'Advertencia')} · desde{' '}
                          {timeAgo(problem.createdAt)}
                        </span>
                        {onRunScript && serviceFromAlertCode(problem.code) && (
                          <StartServiceButton alertId={problem.id} service={serviceFromAlertCode(problem.code)!} />
                        )}
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </Card>

            <Card title="Salud de componentes">
              {machine.snmp && (
                <p className={machine.snmp.lastError ? 'auth-error device-error' : 'muted note'}>
                  {machine.snmp.lastError
                    ? `Último sondeo SNMP fallido: ${machine.snmp.lastError}`
                    : machine.snmp.lastPollAt
                      ? `SNMP ${machine.snmp.version} · último sondeo ${timeAgo(machine.snmp.lastPollAt)}${machine.snmp.collectorName ? ` · lo consulta ${machine.snmp.collectorName}` : ''}`
                      : machine.snmp.collectorName
                        ? `SNMP ${machine.snmp.version}: esperando el primer sondeo de ${machine.snmp.collectorName}.`
                        : `SNMP ${machine.snmp.version}: elija un recolector en Editar para leer el tóner.`}
                </p>
              )}
              {healthRows.length === 0 ? (
                <p className="muted">
                  {machine.snmp
                    ? 'La impresora aún no informó sus consumibles.'
                    : 'El agente aún no envió el inventario de hardware.'}
                </p>
              ) : (
                <ul className="component-list">
                  {healthRows.map((row) => (
                    <li key={row.key}>
                      <Icon name={COMPONENT_ICONS[row.type] ?? 'cpu'} />
                      <div className="component-text">
                        <strong>{row.name}</strong>
                        {row.caption && <span>{row.caption}</span>}
                      </div>
                      <span
                        className="component-state"
                        data-status={row.health === 'desconocido' ? undefined : row.health}
                      >
                        {row.stateLabel ??
                          (row.type === 'antivirus' || row.type === 'firewall'
                            ? SOFTWARE_HEALTH_LABEL
                            : row.type === 'toner'
                              ? SUPPLY_HEALTH_LABEL
                              : HEALTH_LABEL)[row.health]}
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          </div>
        </div>
      )}
    </>
  );
}

/**
 * Filas de "Salud de componentes": hardware (el disco con su detalle SMART), antivirus, firewall y la red,
 * lo que está peor primero. La BIOS y la placa base no tienen salud propia y se omiten.
 */
function componentHealth(machine: MachineDetail, nics: readonly NetworkInterfaceItem[]): HealthRow[] {
  const live = machine.status === 'desconectado' ? null : machine.metrics;
  const rows: HealthRow[] = machine.components
    .filter((c) => c.type !== 'bios' && c.type !== 'placa_base')
    .map((c) => {
      const row: HealthRow = {
        key: `c${c.id}`,
        type: c.type,
        name: COMPONENT_NAMES[c.type] ?? c.type,
        // Un consumible muestra el porcentaje que le queda; el resto, su detalle (SMART, desgaste…).
        caption:
          [c.description, c.type === 'toner' ? (c.capacity ?? c.detail) : (c.detail ?? c.capacity)]
            .filter(Boolean)
            .join(' · ') || null,
        health: c.health,
      };
      return c.health === 'desconocido' ? withoutOwnHealth(row, c.description, live) : row;
    });

  const connected = nics.filter((n) => n.ipAddress && n.type !== 'virtual');
  if (nics.length > 0) {
    const main = connected.find((n) => n.type === 'cableada') ?? connected[0];
    rows.push({
      key: 'red',
      type: 'red',
      name: 'Red',
      caption: main
        ? [
            main.name,
            main.speedMbps && (main.speedMbps >= 1000 ? `${main.speedMbps / 1000} Gbps` : `${main.speedMbps} Mbps`),
          ]
            .filter(Boolean)
            .join(' · ')
        : 'Sin conexión con IP',
      health: main ? 'sano' : 'advertencia',
    });
  }

  return rows.sort((a, b) => HEALTH_ORDER[a.health] - HEALTH_ORDER[b.health]).slice(0, 8);
}

/**
 * Windows no da una "salud" del procesador, la memoria ni la tarjeta de video, y un disco USB no deja leer su SMART:
 * en vez de "Sin datos" se muestra lo que sí se sabe (el uso en vivo) o que ese componente es solo de inventario.
 */
function withoutOwnHealth(row: HealthRow, description: string | null, live: MachineDetail['metrics']): HealthRow {
  const usage = (value: number | null | undefined, what: string): HealthRow | null =>
    value === null || value === undefined
      ? null
      : {
          ...row,
          caption: [description, `${Math.round(value)} % de ${what}`].filter(Boolean).join(' · '),
          health: metricLevel(value),
          stateLabel:
            metricLevel(value) === 'sano' ? 'Normal' : metricLevel(value) === 'advertencia' ? 'Alto' : 'Al límite',
        };
  switch (row.type) {
    case 'cpu':
      return usage(live?.cpu, 'uso') ?? { ...row, stateLabel: 'Solo inventario' };
    case 'ram':
      return usage(live?.ram, 'uso') ?? { ...row, stateLabel: 'Solo inventario' };
    case 'disco':
      return {
        ...row,
        caption: [description, 'Windows no informa su estado (disco USB o controlador RAID)']
          .filter(Boolean)
          .join(' · '),
        stateLabel: 'Sin SMART',
      };
    default:
      return { ...row, stateLabel: 'Solo inventario' };
  }
}

function Breadcrumb({ onBack, branch, name }: { onBack: () => void; branch?: string; name?: string }) {
  return (
    <nav className="breadcrumb" aria-label="Ruta">
      <button type="button" className="link-quiet" onClick={onBack}>
        Equipos
      </button>
      {branch && (
        <>
          <span aria-hidden="true">›</span>
          <span>{branch}</span>
        </>
      )}
      {name && (
        <>
          <span aria-hidden="true">›</span>
          <strong>{name}</strong>
        </>
      )}
    </nav>
  );
}

/** Cinco o siete marcas repartidas en el eje X: horas (1H, 24H) o días (7D). */
function axisLabels(points: readonly MetricPoint[], range: MetricRange): string[] {
  const marks = range === '7d' ? 7 : 5;
  return Array.from({ length: marks }, (_, i) => {
    const point = points[Math.round((i / (marks - 1)) * (points.length - 1))]!;
    if (i === marks - 1 && range !== '7d') return 'Ahora';
    return range === '7d'
      ? new Date(point.at).toLocaleDateString('es-DO', { weekday: 'short' }).replace('.', '').toUpperCase()
      : clock(point.at);
  });
}

interface MoreItem {
  label: string;
  onSelect: () => void;
  danger?: boolean;
}

/** Acciones menos frecuentes de la ficha en un menú, para que la cabecera no se aplaste. */
function MoreActions({ items }: { items: MoreItem[] }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onClick = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onClick);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClick);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  if (items.length === 0) return null;
  return (
    <div className="more-actions" ref={ref}>
      <button
        type="button"
        className="btn btn-outline"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        Más
        <Icon name="dots" size={14} />
      </button>
      {open && (
        <ul className="more-menu" role="menu">
          {items.map((item) => (
            <li key={item.label} role="none">
              <button
                type="button"
                role="menuitem"
                data-danger={item.danger || undefined}
                onClick={() => {
                  setOpen(false);
                  item.onSelect();
                }}
              >
                {item.label}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

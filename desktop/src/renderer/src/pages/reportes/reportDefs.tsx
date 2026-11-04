import type { ReactNode } from 'react';
import {
  STATUS_LABELS,
  type AlertReport,
  type AvailabilityReport,
  type DiskSecurityReport,
  type InventoryReport,
  type MachineStatus,
  type MachineType,
  type PanelApi,
  type PerformanceAdvice,
  type PerformanceReport,
  type RemoteReport,
  type ReportFilter,
  type SecurityIssue,
  type SoftwareReport,
  type TicketPriority,
  type TicketReport,
} from '@monitoreoti/shared';
import { Card } from '@components/Card';
import { EmptyState } from '@components/AsyncState';
import type { IconName } from '@components/Icon';
import { KpiCard } from '@components/KpiCard';
import { LineChart } from '@components/LineChart';
import { StackedBars } from '@components/StackedBars';
import { StatusBadge } from '@components/StatusBadge';
import { dateTime, dayLabel, typeLabel } from '../../data/format';
import { PRIORITY_LABEL } from '../ticketLabels';
import type { CsvTable } from './csv';

export type ReportId =
  'inventario' | 'disponibilidad' | 'alertas' | 'discos-seguridad' | 'rendimiento' | 'software' | 'tickets' | 'remoto';

export interface ReportContext {
  onOpenEquipo: (id: number) => void;
}

export interface ReportDef<T> {
  id: ReportId;
  title: string;
  description: string;
  icon: IconName;
  /** Solo con el módulo reportes_avanzados. */
  advanced: boolean;
  /** Si el reporte depende del periodo (el inventario y los discos son la foto de hoy). */
  period: boolean;
  /** Si tiene buscador (software). */
  search?: boolean;
  load: (api: PanelApi, filter: ReportFilter & { buscar?: string }) => Promise<T>;
  render: (data: T, ctx: ReportContext) => ReactNode;
  csv: (data: T) => CsvTable;
}

const define = <T,>(def: ReportDef<T>) => def as unknown as ReportDef<unknown>;

const pct = (value: number | null | undefined) => (value === null || value === undefined ? '—' : `${value}%`);
const hours = (value: number | null) =>
  value === null ? '—' : value < 1 ? `${Math.round(value * 60)} min` : `${value} h`;
const minutes = (value: number) => (value < 60 ? `${value} min` : `${Math.floor(value / 60)} h ${value % 60} min`);
const day = (iso: string | null) => (iso ? new Date(`${iso.slice(0, 10)}T12:00:00`).toLocaleDateString('es-DO') : '—');
/** "cpu_alto" → "Cpu alto". */
const humanize = (code: string) => (code.charAt(0).toUpperCase() + code.slice(1)).replace(/_/g, ' ');

export const ISSUE_LABEL: Record<SecurityIssue, string> = {
  sin_antivirus: 'Sin antivirus',
  antivirus_desactivado: 'Antivirus desactivado',
  antivirus_desactualizado: 'Antivirus desactualizado',
  firewall_desactivado: 'Firewall desactivado',
};

export const ADVICE_LABEL: Record<PerformanceAdvice, string> = {
  ampliar_ram: 'Ampliar memoria RAM',
  revisar_cpu: 'Revisar el procesador',
  liberar_disco: 'Liberar o ampliar disco',
};

const SESSION_LABEL = { escritorio: 'Escritorio', terminal: 'Terminal', archivos: 'Archivos' } as const;

/** Barras horizontales para conteos (por tipo, por sucursal, por técnico…). */
export function BarList({ items, label }: { items: readonly { key: string; count: number }[]; label: string }) {
  if (items.length === 0) return <p className="muted">Sin datos en el periodo.</p>;
  const max = Math.max(...items.map((i) => i.count));
  return (
    <ul className="bar-list" aria-label={label}>
      {items.map((item) => (
        <li key={item.key}>
          <span className="bar-list-label">{item.key}</span>
          <span className="bar-list-track" aria-hidden="true">
            <span style={{ width: `${(item.count / max) * 100}%` }} />
          </span>
          <strong className="mono">{item.count}</strong>
        </li>
      ))}
    </ul>
  );
}

/** Nombre del equipo como botón que abre su ficha. */
function MachineLink({ id, name, ctx }: { id: number; name: string; ctx: ReportContext }) {
  return (
    <button type="button" className="row-name" onClick={() => ctx.onOpenEquipo(id)}>
      {name}
    </button>
  );
}

// ------------------------------------------------------------------ inventario

const inventory = define<InventoryReport>({
  id: 'inventario',
  title: 'Inventario de equipos',
  description: 'Cada equipo con su hardware, sistema, usuario, serie y red.',
  icon: 'monitor',
  advanced: false,
  period: false,
  load: (api, f) => api.reportes.inventory(f),
  render: (data, ctx) => (
    <>
      <div className="kpi-grid report-kpis">
        <KpiCard label="Equipos" value={data.total} />
        {data.byType.slice(0, 3).map((t) => (
          <KpiCard key={t.key} label={typeLabel(t.key as MachineType)} value={t.count} />
        ))}
      </div>
      <div className="report-two">
        <Card title="Por tipo">
          <BarList
            label="Equipos por tipo"
            items={data.byType.map((t) => ({ ...t, key: typeLabel(t.key as MachineType) }))}
          />
        </Card>
        <Card title="Por sistema operativo">
          <BarList label="Equipos por sistema operativo" items={data.byOperatingSystem.slice(0, 8)} />
        </Card>
      </div>
      <div className="table-card report-table">
        <table className="data-table data-table-compact">
          <thead>
            <tr>
              <th>Equipo</th>
              <th>Sucursal</th>
              <th>Usuario</th>
              <th>Sistema</th>
              <th>Procesador</th>
              <th>RAM</th>
              <th>Discos</th>
              <th>Serie</th>
              <th>IP</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {data.rows.map((m) => (
              <tr key={m.id}>
                <td>
                  <MachineLink id={m.id} name={m.name} ctx={ctx} />
                  <span className="muted report-sub">{typeLabel(m.type as MachineType)}</span>
                </td>
                <td>
                  {m.branchName}
                  {m.departmentName && <span className="muted report-sub">{m.departmentName}</span>}
                </td>
                <td>{m.assignedTo ?? '—'}</td>
                <td>{m.operatingSystem ?? '—'}</td>
                <td>{m.processor ?? '—'}</td>
                <td className="mono">{m.ramGb !== null ? `${m.ramGb} GB` : '—'}</td>
                <td>{m.disks ?? '—'}</td>
                <td className="mono">{m.serialNumber ?? '—'}</td>
                <td className="mono">{m.ipAddress ?? '—'}</td>
                <td>
                  <StatusBadge status={m.status as MachineStatus} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {data.rows.length === 0 && <EmptyState title="No hay equipos con estos filtros" />}
      </div>
    </>
  ),
  csv: (data) => ({
    columns: [
      'Equipo',
      'Tipo',
      'Sucursal',
      'Departamento',
      'Usuario',
      'Sistema operativo',
      'Procesador',
      'RAM (GB)',
      'Discos',
      'Marca',
      'Modelo',
      'Serie',
      'IP',
      'MAC',
      'Estado',
      'Garantía hasta',
      'Alta',
      'Último reporte',
    ],
    rows: data.rows.map((m) => [
      m.name,
      typeLabel(m.type as MachineType),
      m.branchName,
      m.departmentName,
      m.assignedTo,
      m.operatingSystem,
      m.processor,
      m.ramGb,
      m.disks,
      m.brand,
      m.model,
      m.serialNumber,
      m.ipAddress,
      m.macAddress,
      STATUS_LABELS[m.status as MachineStatus] ?? m.status,
      m.warrantyUntil,
      dateTime(m.createdAt),
      dateTime(m.lastReportAt),
    ]),
  }),
});

// ------------------------------------------------------------------ disponibilidad

const availability = define<AvailabilityReport>({
  id: 'disponibilidad',
  title: 'Disponibilidad',
  description: 'Cuánto tiempo estuvo en línea cada equipo y el estado de la flota día a día.',
  icon: 'history',
  advanced: false,
  period: true,
  load: (api, f) => api.reportes.availability(f),
  render: (data, ctx) => {
    const below = data.machines.filter((m) => m.percent < 90).length;
    const max = Math.max(1, ...data.days.map((d) => d.healthy + d.warning + d.critical + d.disconnected));
    return (
      <>
        <div className="kpi-grid report-kpis">
          <KpiCard
            label="Disponibilidad promedio"
            value={data.averagePercent}
            unit="%"
            tone={data.averagePercent >= 95 ? 'sano' : 'advertencia'}
          />
          <KpiCard label="Equipos medidos" value={data.machines.length} />
          <KpiCard label="Por debajo de 90%" value={below} tone={below > 0 ? 'advertencia' : 'neutral'} />
        </div>
        {data.days.length > 0 && (
          <Card title="Estado de la flota por día">
            <StackedBars
              max={max}
              columns={data.days.map((d) => ({
                label: dayLabel(d.date),
                sano: d.healthy,
                advertencia: d.warning,
                critico: d.critical,
                desconectado: d.disconnected,
              }))}
            />
          </Card>
        )}
        <div className="table-card report-table">
          <table className="data-table data-table-compact">
            <thead>
              <tr>
                <th>Equipo</th>
                <th>Sucursal</th>
                <th>En línea</th>
                <th>Horas con datos</th>
                <th>Último reporte</th>
              </tr>
            </thead>
            <tbody>
              {data.machines.map((m) => (
                <tr key={m.id}>
                  <td>
                    <MachineLink id={m.id} name={m.name} ctx={ctx} />
                  </td>
                  <td>{m.branchName}</td>
                  <td>
                    <span
                      className="usage-bar report-usage"
                      data-level={m.percent < 80 ? 'critico' : m.percent < 95 ? 'advertencia' : undefined}
                    >
                      <span style={{ width: `${m.percent}%` }} />
                    </span>
                    <span className="mono">{m.percent}%</span>
                  </td>
                  <td className="mono">
                    {m.hoursOnline} / {m.hoursInPeriod}
                  </td>
                  <td>{dateTime(m.lastReportAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.machines.length === 0 && <EmptyState title="No hay equipos en el periodo" />}
        </div>
      </>
    );
  },
  csv: (data) => ({
    columns: ['Equipo', 'Sucursal', 'En línea (%)', 'Horas con datos', 'Horas del periodo', 'Último reporte'],
    rows: data.machines.map((m) => [
      m.name,
      m.branchName,
      m.percent,
      m.hoursOnline,
      m.hoursInPeriod,
      dateTime(m.lastReportAt),
    ]),
  }),
});

// ------------------------------------------------------------------ alertas

const alerts = define<AlertReport>({
  id: 'alertas',
  title: 'Alertas',
  description: 'Cuántas hubo, de qué tipo, dónde y cuánto tardaron en resolverse.',
  icon: 'bell',
  advanced: false,
  period: true,
  load: (api, f) => api.reportes.alerts(f),
  render: (data, ctx) => {
    const max = Math.max(1, ...data.perDay.map((d) => d.critical + d.warning));
    return (
      <>
        <div className="kpi-grid report-kpis">
          <KpiCard label="Alertas" value={data.total} />
          <KpiCard label="Críticas" value={data.critical} tone={data.critical > 0 ? 'critico' : 'neutral'} />
          <KpiCard label="Siguen abiertas" value={data.open} tone={data.open > 0 ? 'advertencia' : 'neutral'} />
          <KpiCard
            label="Tiempo medio de resolución"
            value={data.averageResolutionHours ?? 0}
            unit="h"
            note={data.averageResolutionHours === null ? 'Ninguna resuelta todavía' : undefined}
          />
        </div>
        {data.perDay.length > 0 && (
          <Card title="Alertas por día">
            <StackedBars
              max={max}
              columns={data.perDay.map((d) => ({
                label: dayLabel(d.date),
                sano: 0,
                advertencia: d.warning,
                critico: d.critical,
                desconectado: 0,
              }))}
            />
          </Card>
        )}
        <div className="report-two">
          <Card title="Por tipo">
            <BarList label="Alertas por tipo" items={data.byCode.map((c) => ({ ...c, key: humanize(c.key) }))} />
          </Card>
          <Card title="Por sucursal">
            <BarList label="Alertas por sucursal" items={data.byBranch} />
          </Card>
        </div>
        <Card title="Equipos con más alertas">
          {data.topMachines.length === 0 ? (
            <p className="muted">Sin alertas en el periodo.</p>
          ) : (
            <table className="data-table data-table-compact">
              <thead>
                <tr>
                  <th>Equipo</th>
                  <th>Sucursal</th>
                  <th>Alertas</th>
                  <th>Críticas</th>
                </tr>
              </thead>
              <tbody>
                {data.topMachines.map((m) => (
                  <tr key={m.id}>
                    <td>
                      <MachineLink id={m.id} name={m.name} ctx={ctx} />
                    </td>
                    <td>{m.branchName}</td>
                    <td className="mono">{m.count}</td>
                    <td className="mono">{m.critical}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </Card>
      </>
    );
  },
  csv: (data) => ({
    columns: ['Día', 'Críticas', 'Advertencias'],
    rows: data.perDay.map((d) => [d.date, d.critical, d.warning]),
  }),
});

// ------------------------------------------------------------------ discos y seguridad

const diskSecurity = define<DiskSecurityReport>({
  id: 'discos-seguridad',
  title: 'Discos y seguridad',
  description: 'Discos casi llenos o con fallas, y equipos sin antivirus o con el firewall apagado.',
  icon: 'shield',
  advanced: false,
  period: false,
  load: (api, f) => api.reportes.diskSecurity(f),
  render: (data, ctx) => (
    <>
      <div className="kpi-grid report-kpis">
        <KpiCard
          label="Discos con menos de 15% libre"
          value={data.lowDisks.length}
          tone={data.lowDisks.length ? 'advertencia' : 'sano'}
        />
        <KpiCard
          label="Discos con fallas"
          value={data.diskHealth.length}
          tone={data.diskHealth.length ? 'critico' : 'sano'}
        />
        <KpiCard
          label="Problemas de seguridad"
          value={data.security.length}
          tone={data.security.length ? 'critico' : 'sano'}
        />
      </div>
      <Card title="Discos casi llenos">
        {data.lowDisks.length === 0 ? (
          <p className="muted">Ningún disco por debajo de 15% libre.</p>
        ) : (
          <table className="data-table data-table-compact">
            <thead>
              <tr>
                <th>Equipo</th>
                <th>Sucursal</th>
                <th>Unidad</th>
                <th>Libre</th>
              </tr>
            </thead>
            <tbody>
              {data.lowDisks.map((d) => (
                <tr key={`${d.machineId}-${d.drive}`}>
                  <td>
                    <MachineLink id={d.machineId} name={d.machineName} ctx={ctx} />
                  </td>
                  <td>{d.branchName}</td>
                  <td className="mono">{d.drive}</td>
                  <td className="mono">
                    {d.freeGb} de {d.totalGb} GB ({d.freePercent}%)
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
      <div className="report-two">
        <Card title="Salud de los discos">
          {data.diskHealth.length === 0 ? (
            <p className="muted">Ningún disco con fallas.</p>
          ) : (
            <ul className="report-issues">
              {data.diskHealth.map((d, i) => (
                <li key={i} data-status={d.health}>
                  <span className="status-dot" aria-hidden="true" />
                  <span>
                    <MachineLink id={d.machineId} name={d.machineName} ctx={ctx} />
                    <span className="muted report-sub">
                      {d.disk ?? 'Disco'} · {d.detail ?? STATUS_LABELS[d.health as MachineStatus]}
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>
        <Card title="Seguridad">
          {data.security.length === 0 ? (
            <p className="muted">Todos los equipos con antivirus y firewall activos.</p>
          ) : (
            <ul className="report-issues">
              {data.security.map((s, i) => (
                <li key={i} data-status={s.issue === 'antivirus_desactualizado' ? 'advertencia' : 'critico'}>
                  <span className="status-dot" aria-hidden="true" />
                  <span>
                    <MachineLink id={s.machineId} name={s.machineName} ctx={ctx} />
                    <span className="muted report-sub">
                      {ISSUE_LABEL[s.issue]}
                      {s.detail && ` · ${s.detail}`} · {s.branchName}
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
    </>
  ),
  csv: (data) => ({
    columns: ['Equipo', 'Sucursal', 'Problema', 'Detalle'],
    rows: [
      ...data.lowDisks.map((d) => [
        d.machineName,
        d.branchName,
        `Disco ${d.drive} casi lleno`,
        `${d.freeGb} de ${d.totalGb} GB libres (${d.freePercent}%)`,
      ]),
      ...data.diskHealth.map((d) => [
        d.machineName,
        d.branchName,
        'Disco con fallas',
        [d.disk, d.detail].filter(Boolean).join(' · '),
      ]),
      ...data.security.map((s) => [s.machineName, s.branchName, ISSUE_LABEL[s.issue], s.detail]),
    ],
  }),
});

// ------------------------------------------------------------------ rendimiento (avanzado)

const performance = define<PerformanceReport>({
  id: 'rendimiento',
  title: 'Rendimiento',
  description: 'Uso de CPU, memoria y disco por equipo, y cuáles conviene ampliar o cambiar.',
  icon: 'cpu',
  advanced: true,
  period: true,
  load: (api, f) => api.reportes.performance(f),
  render: (data, ctx) => {
    const flagged = data.machines.filter((m) => m.advice.length > 0).length;
    return (
      <>
        <div className="kpi-grid report-kpis">
          <KpiCard label="Equipos medidos" value={data.machines.length} />
          <KpiCard label="Conviene revisar" value={flagged} tone={flagged > 0 ? 'advertencia' : 'sano'} />
        </div>
        {data.trend.length > 1 && (
          <Card title="Uso promedio de la flota">
            <LineChart
              xLabels={data.trend.map((d) => dayLabel(d.date))}
              series={[
                { name: 'CPU', color: 'cpu', values: data.trend.map((d) => d.cpu ?? 0) },
                { name: 'RAM', color: 'ram', values: data.trend.map((d) => d.ram ?? 0), area: true },
              ]}
            />
          </Card>
        )}
        <div className="table-card report-table">
          <table className="data-table data-table-compact">
            <thead>
              <tr>
                <th>Equipo</th>
                <th>Sucursal</th>
                <th>CPU prom. / máx.</th>
                <th>RAM prom. / máx.</th>
                <th>Disco máx.</th>
                <th>Recomendación</th>
              </tr>
            </thead>
            <tbody>
              {data.machines.map((m) => (
                <tr key={m.id}>
                  <td>
                    <MachineLink id={m.id} name={m.name} ctx={ctx} />
                  </td>
                  <td>{m.branchName}</td>
                  <td className="mono">
                    {pct(m.cpuAverage)} / {pct(m.cpuPeak)}
                  </td>
                  <td className="mono">
                    {pct(m.ramAverage)} / {pct(m.ramPeak)}
                  </td>
                  <td className="mono">{pct(m.diskMax)}</td>
                  <td>
                    {m.advice.length === 0 ? (
                      <span className="muted">—</span>
                    ) : (
                      m.advice.map((a) => (
                        <span key={a} className="report-chip">
                          {ADVICE_LABEL[a]}
                        </span>
                      ))
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {data.machines.length === 0 && <EmptyState title="No hay métricas en el periodo" />}
        </div>
      </>
    );
  },
  csv: (data) => ({
    columns: [
      'Equipo',
      'Sucursal',
      'CPU promedio (%)',
      'CPU máximo (%)',
      'RAM promedio (%)',
      'RAM máximo (%)',
      'Disco máximo (%)',
      'Horas medidas',
      'Recomendación',
    ],
    rows: data.machines.map((m) => [
      m.name,
      m.branchName,
      m.cpuAverage,
      m.cpuPeak,
      m.ramAverage,
      m.ramPeak,
      m.diskMax,
      m.hours,
      m.advice.map((a) => ADVICE_LABEL[a]).join('; '),
    ]),
  }),
});

// ------------------------------------------------------------------ software (avanzado)

const software = define<SoftwareReport>({
  id: 'software',
  title: 'Software instalado',
  description: 'Cada programa con cuántos equipos lo tienen y en qué versiones. Busque uno para ver quién lo tiene.',
  icon: 'download',
  advanced: true,
  period: false,
  search: true,
  load: (api, f) => api.reportes.software(f),
  render: (data, ctx) => (
    <>
      <div className="kpi-grid report-kpis">
        <KpiCard label="Programas distintos" value={data.programs} />
      </div>
      {data.holders.length > 0 && (
        <Card title="Equipos que lo tienen">
          <table className="data-table data-table-compact">
            <thead>
              <tr>
                <th>Equipo</th>
                <th>Sucursal</th>
                <th>Versión</th>
                <th>Instalado</th>
              </tr>
            </thead>
            <tbody>
              {data.holders.map((h, i) => (
                <tr key={i}>
                  <td>
                    <MachineLink id={h.machineId} name={h.machineName} ctx={ctx} />
                  </td>
                  <td>{h.branchName}</td>
                  <td className="mono">{h.version ?? '—'}</td>
                  <td>{day(h.installedOn)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
      <div className="table-card report-table">
        <table className="data-table data-table-compact">
          <thead>
            <tr>
              <th>Programa</th>
              <th>Editor</th>
              <th>Equipos</th>
              <th>Versiones</th>
            </tr>
          </thead>
          <tbody>
            {data.rows.map((r) => (
              <tr key={r.name}>
                <td>{r.name}</td>
                <td>{r.publisher ?? '—'}</td>
                <td className="mono">{r.machines}</td>
                <td>
                  {r.versions.slice(0, 4).map((v) => (
                    <span key={v.key} className="report-chip mono">
                      {v.key} · {v.count}
                    </span>
                  ))}
                  {r.versions.length > 4 && <span className="muted"> +{r.versions.length - 4}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {data.rows.length === 0 && <EmptyState title="Ningún programa coincide" />}
      </div>
    </>
  ),
  csv: (data) =>
    data.holders.length > 0
      ? {
          columns: ['Equipo', 'Sucursal', 'Versión', 'Instalado'],
          rows: data.holders.map((h) => [h.machineName, h.branchName, h.version, h.installedOn]),
        }
      : {
          columns: ['Programa', 'Editor', 'Equipos', 'Versiones'],
          rows: data.rows.map((r) => [
            r.name,
            r.publisher,
            r.machines,
            r.versions.map((v) => `${v.key} (${v.count})`).join('; '),
          ]),
        },
});

// ------------------------------------------------------------------ tickets (avanzado)

const tickets = define<TicketReport>({
  id: 'tickets',
  title: 'Tickets y mantenimiento',
  description: 'Tickets abiertos y cerrados, tiempo de solución, carga por técnico y mantenimientos al día.',
  icon: 'ticket',
  advanced: true,
  period: true,
  load: (api, f) => api.reportes.tickets(f),
  render: (data) => (
    <>
      <div className="kpi-grid report-kpis">
        <KpiCard label="Creados" value={data.created} />
        <KpiCard label="Cerrados" value={data.closed} tone="sano" />
        <KpiCard label="Abiertos hoy" value={data.open} tone={data.open > 0 ? 'advertencia' : 'neutral'} />
        <KpiCard
          label="Tiempo medio de solución"
          value={data.averageResolutionHours ?? 0}
          unit="h"
          note={data.averageResolutionHours === null ? 'Ninguno cerrado en el periodo' : undefined}
        />
      </div>
      <div className="report-two">
        <Card title="Por prioridad">
          <BarList
            label="Tickets por prioridad"
            items={data.byPriority.map((p) => ({ ...p, key: PRIORITY_LABEL[p.key as TicketPriority] ?? p.key }))}
          />
        </Card>
        <Card title="Mantenimientos del periodo">
          <div className="report-maintenance">
            <KpiCard label="Hechos" value={data.maintenance.done} tone="sano" />
            <KpiCard label="Pendientes" value={data.maintenance.pending} />
            <KpiCard
              label="Vencidos"
              value={data.maintenance.overdue}
              tone={data.maintenance.overdue > 0 ? 'critico' : 'neutral'}
            />
          </div>
        </Card>
      </div>
      <Card title="Carga por técnico">
        {data.technicians.length === 0 ? (
          <p className="muted">Ningún ticket asignado en el periodo.</p>
        ) : (
          <table className="data-table data-table-compact">
            <thead>
              <tr>
                <th>Técnico</th>
                <th>Asignados</th>
                <th>Cerrados</th>
                <th>Tiempo medio</th>
              </tr>
            </thead>
            <tbody>
              {data.technicians.map((t) => (
                <tr key={t.name}>
                  <td>{t.name}</td>
                  <td className="mono">{t.assigned}</td>
                  <td className="mono">{t.closed}</td>
                  <td className="mono">{hours(t.averageResolutionHours)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </>
  ),
  csv: (data) => ({
    columns: ['Técnico', 'Asignados', 'Cerrados', 'Tiempo medio (h)'],
    rows: data.technicians.map((t) => [t.name, t.assigned, t.closed, t.averageResolutionHours]),
  }),
});

// ------------------------------------------------------------------ accesos remotos (avanzado)

const remote = define<RemoteReport>({
  id: 'remoto',
  title: 'Accesos remotos',
  description: 'Quién se conectó a qué equipo, cuánto tiempo, en qué modo y por qué.',
  icon: 'remote',
  advanced: true,
  period: true,
  load: (api, f) => api.reportes.remote(f),
  render: (data, ctx) => (
    <>
      <div className="kpi-grid report-kpis">
        <KpiCard label="Sesiones" value={data.sessions} />
        <KpiCard label="Tiempo conectado" value={Math.round(data.minutes / 60)} unit="h" />
        <KpiCard label="Técnicos" value={data.technicians.length} />
      </div>
      <Card title="Por técnico">
        <BarList
          label="Sesiones por técnico"
          items={data.technicians.map((t) => ({ key: t.name, count: t.sessions }))}
        />
      </Card>
      <div className="table-card report-table">
        <table className="data-table data-table-compact">
          <thead>
            <tr>
              <th>Inicio</th>
              <th>Técnico</th>
              <th>Equipo</th>
              <th>Tipo</th>
              <th>Duración</th>
              <th>Motivo</th>
            </tr>
          </thead>
          <tbody>
            {data.rows.map((s) => (
              <tr key={s.id}>
                <td>{dateTime(s.startedAt)}</td>
                <td>{s.userName}</td>
                <td>
                  <MachineLink id={s.machineId} name={s.machineName} ctx={ctx} />
                  <span className="muted report-sub">{s.branchName}</span>
                </td>
                <td>
                  {SESSION_LABEL[s.type]}
                  {s.viewOnly && <span className="muted"> · solo ver</span>}
                </td>
                <td className="mono">{s.endedAt ? minutes(s.minutes) : 'En curso'}</td>
                <td>{s.reason ?? '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {data.rows.length === 0 && <EmptyState title="No hubo sesiones remotas en el periodo" />}
      </div>
    </>
  ),
  csv: (data) => ({
    columns: ['Inicio', 'Fin', 'Minutos', 'Técnico', 'Equipo', 'Sucursal', 'Tipo', 'Solo ver', 'Motivo'],
    rows: data.rows.map((s) => [
      dateTime(s.startedAt),
      dateTime(s.endedAt),
      s.minutes,
      s.userName,
      s.machineName,
      s.branchName,
      SESSION_LABEL[s.type],
      s.viewOnly,
      s.reason,
    ]),
  }),
});

export const REPORTS: readonly ReportDef<unknown>[] = [
  inventory,
  availability,
  alerts,
  diskSecurity,
  performance,
  software,
  tickets,
  remote,
];

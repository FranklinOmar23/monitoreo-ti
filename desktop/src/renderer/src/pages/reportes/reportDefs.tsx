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
  type ReportSummary,
  type SecurityIssue,
  type SoftwareReport,
  type SummaryKpiKey,
  type TicketPriority,
  type TicketReport,
} from '@monitoreoti/shared';
import { Card } from '@components/Card';
import type { IconName } from '@components/Icon';
import { LineChart } from '@components/LineChart';
import { StackedBars } from '@components/StackedBars';
import { StatusBadge } from '@components/StatusBadge';
import { dateTime, dayLabel, typeLabel } from '../../data/format';
import { PRIORITY_LABEL } from '../ticketLabels';
import type { CsvTable } from './csv';
import { ReportTable } from './ReportTable';
import { findKpi, KPI_META } from './summaryLabels';
import { BarList, DeltaKpi, Heatmap, Insights, num, PercentBar, type Insight, type Tone } from './widgets';

export type ReportId =
  'inventario' | 'disponibilidad' | 'alertas' | 'discos-seguridad' | 'rendimiento' | 'software' | 'tickets' | 'remoto';

export interface ReportContext {
  onOpenEquipo: (id: number) => void;
  /** Resumen del mismo periodo y filtros: da el valor del periodo anterior de cada KPI (puede faltar). */
  summary?: ReportSummary;
}

export interface ReportDef<T> {
  id: ReportId;
  title: string;
  /** Nombre corto para el menú lateral. */
  short: string;
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

const pct = (value: number | null | undefined) => (value === null || value === undefined ? '—' : `${num(value)}%`);
const hours = (value: number | null) =>
  value === null ? '—' : value < 1 ? `${Math.round(value * 60)} min` : `${num(value)} h`;
const minutes = (value: number) => (value < 60 ? `${value} min` : `${Math.floor(value / 60)} h ${value % 60} min`);
const day = (iso: string | null) => (iso ? new Date(`${iso.slice(0, 10)}T12:00:00`).toLocaleDateString('es-DO') : '—');
/** "cpu_alto" → "Cpu alto". */
const humanize = (code: string) => (code.charAt(0).toUpperCase() + code.slice(1)).replace(/_/g, ' ');
const share = (part: number, total: number) => (total === 0 ? 0 : Math.round((part / total) * 100));
const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;
const usageLevel = (value: number | null): Tone | undefined =>
  value === null ? undefined : value >= 85 ? 'critico' : value >= 70 ? 'advertencia' : 'sano';

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

/** Nombre del equipo como botón que abre su ficha. */
function MachineLink({ id, name, ctx }: { id: number; name: string; ctx: ReportContext }) {
  return (
    <button type="button" className="row-name mono" onClick={() => ctx.onOpenEquipo(id)}>
      {name}
    </button>
  );
}

/**
 * KPI con la variación frente al periodo anterior tomada del resumen. `value` es el del reporte (coincide con el del
 * resumen porque el servidor usa la misma consulta).
 */
function Kpi({
  ctx,
  id,
  value,
  label,
  unit,
  tone,
  note,
  trend = true,
}: {
  ctx: ReportContext;
  id?: SummaryKpiKey;
  value: number | null;
  label?: string;
  unit?: string;
  tone?: Tone;
  note?: ReactNode;
  trend?: boolean;
}) {
  const kpi = id ? findKpi(ctx.summary?.kpis, id) : undefined;
  const meta = id ? KPI_META[id] : undefined;
  return (
    <DeltaKpi
      label={label ?? meta?.label ?? ''}
      value={value}
      unit={unit ?? meta?.unit}
      previous={kpi?.previous}
      better={meta?.better}
      series={trend ? kpi?.series : undefined}
      tone={tone ?? (value !== null && meta?.tone ? meta.tone(value) : 'neutral')}
      note={note}
    />
  );
}

// ------------------------------------------------------------------ inventario

const inventory = define<InventoryReport>({
  id: 'inventario',
  title: 'Inventario de equipos',
  short: 'Inventario',
  description: 'Cada equipo con su hardware, sistema, usuario, serie y red.',
  icon: 'monitor',
  advanced: false,
  period: false,
  load: (api, f) => api.reportes.inventory(f),
  render: (data, ctx) => {
    const today = new Date().toISOString().slice(0, 10);
    const expired = data.rows.filter((m) => m.warrantyUntil && m.warrantyUntil < today).length;
    const unhealthy = data.rows.filter((m) => m.status !== 'sano').length;
    const noUser = data.rows.filter((m) => !m.assignedTo).length;
    const lowRam = data.rows.filter((m) => m.ramGb !== null && m.ramGb < 8).length;
    const byBranch = Object.entries(
      data.rows.reduce<Record<string, number>>(
        (acc, m) => ({ ...acc, [m.branchName]: (acc[m.branchName] ?? 0) + 1 }),
        {},
      ),
    )
      .map(([key, count]) => ({ key, count }))
      .sort((a, b) => b.count - a.count);
    const topOs = data.byOperatingSystem[0];
    const insights: Insight[] = [];
    if (unhealthy)
      insights.push({
        level: 'advertencia',
        text: `${plural(unhealthy, 'equipo no está', 'equipos no están')} en estado sano ahora mismo.`,
      });
    if (expired)
      insights.push({
        level: 'advertencia',
        text: `${plural(expired, 'equipo tiene', 'equipos tienen')} la garantía vencida.`,
      });
    if (lowRam)
      insights.push({
        level: 'info',
        text: `${plural(lowRam, 'equipo tiene', 'equipos tienen')} menos de 8 GB de RAM.`,
      });
    if (noUser)
      insights.push({
        level: 'info',
        text: `${plural(noUser, 'equipo no tiene', 'equipos no tienen')} usuario asignado.`,
      });
    if (topOs && data.total)
      insights.push({
        level: 'info',
        text: `${topOs.key} es el sistema más común (${share(topOs.count, data.total)}% de la flota).`,
      });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi ctx={ctx} id="equipos" value={data.total} trend={false} note="Foto de hoy" />
          <Kpi ctx={ctx} label="No sanos ahora" value={unhealthy} tone={unhealthy ? 'advertencia' : 'sano'} />
          <Kpi ctx={ctx} label="Garantía vencida" value={expired} tone={expired ? 'advertencia' : 'neutral'} />
          <Kpi ctx={ctx} label="Sin usuario asignado" value={noUser} />
        </div>
        <Insights items={insights} />
        <div className="report-three">
          <Card title="Por tipo">
            <BarList
              label="Equipos por tipo"
              total={data.total}
              items={data.byType.map((t) => ({ ...t, key: typeLabel(t.key as MachineType) }))}
            />
          </Card>
          <Card title="Por sistema operativo">
            <BarList
              label="Equipos por sistema operativo"
              total={data.total}
              items={data.byOperatingSystem.slice(0, 6)}
            />
          </Card>
          <Card title="Por sucursal">
            <BarList label="Equipos por sucursal" total={data.total} items={byBranch.slice(0, 6)} />
          </Card>
        </div>
        <ReportTable
          title="Equipos"
          noun="equipos"
          empty="No hay equipos con estos filtros"
          rows={data.rows}
          rowKey={(m) => m.id}
          problem={(m) => m.status !== 'sano' || Boolean(m.warrantyUntil && m.warrantyUntil < today)}
          columns={[
            {
              key: 'name',
              label: 'Equipo',
              sort: (m) => m.name,
              render: (m) => (
                <>
                  <MachineLink id={m.id} name={m.name} ctx={ctx} />
                  <span className="muted report-sub">{typeLabel(m.type as MachineType)}</span>
                </>
              ),
            },
            {
              key: 'branch',
              label: 'Sucursal',
              sort: (m) => m.branchName,
              render: (m) => (
                <>
                  {m.branchName}
                  {m.departmentName && <span className="muted report-sub">{m.departmentName}</span>}
                </>
              ),
            },
            { key: 'user', label: 'Usuario', sort: (m) => m.assignedTo, render: (m) => m.assignedTo ?? '—' },
            { key: 'os', label: 'Sistema', sort: (m) => m.operatingSystem, render: (m) => m.operatingSystem ?? '—' },
            { key: 'cpu', label: 'Procesador', sort: (m) => m.processor, render: (m) => m.processor ?? '—' },
            {
              key: 'ram',
              label: 'RAM',
              numeric: true,
              sort: (m) => m.ramGb,
              render: (m) => <span className="mono">{m.ramGb !== null ? `${m.ramGb} GB` : '—'}</span>,
            },
            { key: 'disks', label: 'Discos', render: (m) => m.disks ?? '—' },
            {
              key: 'serial',
              label: 'Serie',
              sort: (m) => m.serialNumber,
              render: (m) => <span className="mono">{m.serialNumber ?? '—'}</span>,
            },
            {
              key: 'ip',
              label: 'IP',
              sort: (m) => m.ipAddress,
              render: (m) => <span className="mono">{m.ipAddress ?? '—'}</span>,
            },
            {
              key: 'warranty',
              label: 'Garantía',
              sort: (m) => m.warrantyUntil,
              render: (m) => (
                <span className={m.warrantyUntil && m.warrantyUntil < today ? 'report-expired' : undefined}>
                  {day(m.warrantyUntil)}
                </span>
              ),
            },
            {
              key: 'status',
              label: 'Estado',
              sort: (m) => ['critico', 'advertencia', 'desconectado', 'sano'].indexOf(m.status),
              render: (m) => <StatusBadge status={m.status as MachineStatus} />,
            },
          ]}
        />
      </>
    );
  },
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
  short: 'Disponibilidad',
  description: 'Cuánto tiempo estuvo en línea cada equipo y el estado de la flota día a día.',
  icon: 'history',
  advanced: false,
  period: true,
  load: (api, f) => api.reportes.availability(f),
  render: (data, ctx) => {
    const below = data.machines.filter((m) => m.percent < 90).length;
    const full = data.machines.filter((m) => m.percent >= 99).length;
    const hoursLost = data.machines.reduce((sum, m) => sum + (m.hoursInPeriod - m.hoursOnline), 0);
    const worst = data.machines[0];
    const max = Math.max(1, ...data.days.map((d) => d.healthy + d.warning + d.critical + d.disconnected));
    // El peor día de la flota: donde el promedio fue más bajo.
    const worstDay = data.fleetTrend.reduce<{ i: number; v: number } | null>(
      (acc, v, i) => (v !== null && (acc === null || v < acc.v) ? { i, v } : acc),
      null,
    );

    const insights: Insight[] = [];
    if (below)
      insights.push({
        level: 'advertencia',
        text: `${plural(below, 'equipo estuvo', 'equipos estuvieron')} en línea menos del 90% del tiempo.`,
      });
    if (worst && worst.percent < 90)
      insights.push({
        level: worst.percent < 80 ? 'critico' : 'advertencia',
        text: `${worst.name} es el más afectado: ${num(worst.percent)}% en línea.`,
      });
    if (worstDay && worstDay.v < 95)
      insights.push({
        level: 'info',
        text: `El ${data.bucketDays === 1 ? 'peor día' : 'peor tramo'} fue el ${day(data.buckets[worstDay.i] ?? null)}, con la flota al ${num(worstDay.v)}%.`,
      });
    if (data.machines.length && !below)
      insights.push({ level: 'sano', text: 'Todos los equipos estuvieron en línea al menos el 90% del tiempo.' });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi
            ctx={ctx}
            id="disponibilidad"
            value={data.machines.length ? data.averagePercent : null}
            label="Disponibilidad promedio"
          />
          <Kpi ctx={ctx} label="Por debajo de 90%" value={below} tone={below ? 'advertencia' : 'sano'} />
          <Kpi ctx={ctx} label="Al 99% o más" value={full} tone="neutral" />
          <Kpi ctx={ctx} label="Horas sin datos" value={hoursLost} unit="h" />
        </div>
        <Insights items={insights} />
        {data.machines.length > 0 && (
          <Card
            title={data.bucketDays === 1 ? 'En línea por equipo y día' : 'En línea por equipo y semana'}
            className="report-main-chart"
          >
            <Heatmap
              rows={data.machines}
              buckets={data.buckets}
              bucketDays={data.bucketDays}
              onOpen={ctx.onOpenEquipo}
            />
          </Card>
        )}
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
        <ReportTable
          title="Equipos"
          noun="equipos"
          empty="No hay equipos en el periodo"
          rows={data.machines}
          rowKey={(m) => m.id}
          problem={(m) => m.percent < 90}
          columns={[
            {
              key: 'name',
              label: 'Equipo',
              sort: (m) => m.name,
              render: (m) => <MachineLink id={m.id} name={m.name} ctx={ctx} />,
            },
            { key: 'branch', label: 'Sucursal', sort: (m) => m.branchName, render: (m) => m.branchName },
            {
              key: 'percent',
              label: 'En línea',
              sort: (m) => m.percent,
              render: (m) => (
                <PercentBar
                  value={m.percent}
                  level={m.percent < 80 ? 'critico' : m.percent < 95 ? 'advertencia' : 'sano'}
                />
              ),
            },
            {
              key: 'hours',
              label: 'Horas con datos',
              numeric: true,
              sort: (m) => m.hoursOnline,
              render: (m) => (
                <span className="mono">
                  {m.hoursOnline} / {m.hoursInPeriod}
                </span>
              ),
            },
            {
              key: 'last',
              label: 'Último reporte',
              sort: (m) => m.lastReportAt,
              render: (m) => dateTime(m.lastReportAt),
            },
          ]}
        />
      </>
    );
  },
  csv: (data) => ({
    columns: [
      'Equipo',
      'Sucursal',
      'En línea (%)',
      'Horas con datos',
      'Horas del periodo',
      'Último reporte',
      ...data.buckets.map((b) => `${data.bucketDays === 1 ? 'Día' : 'Semana'} ${b} (%)`),
    ],
    rows: data.machines.map((m) => [
      m.name,
      m.branchName,
      m.percent,
      m.hoursOnline,
      m.hoursInPeriod,
      dateTime(m.lastReportAt),
      ...m.cells,
    ]),
  }),
});

// ------------------------------------------------------------------ alertas

const alerts = define<AlertReport>({
  id: 'alertas',
  title: 'Alertas',
  short: 'Alertas',
  description: 'Cuántas hubo, de qué tipo, dónde y cuánto tardaron en resolverse.',
  icon: 'bell',
  advanced: false,
  period: true,
  load: (api, f) => api.reportes.alerts(f),
  render: (data, ctx) => {
    const max = Math.max(1, ...data.perDay.map((d) => d.critical + d.warning));
    const topType = data.byCode[0];
    const topBranch = data.byBranch[0];
    const topMachine = data.topMachines[0];
    const busiest = data.perDay.reduce<(typeof data.perDay)[number] | null>(
      (acc, d) => (!acc || d.critical + d.warning > acc.critical + acc.warning ? d : acc),
      null,
    );
    const insights: Insight[] = [];
    if (data.total === 0) insights.push({ level: 'sano', text: 'No hubo alertas en el periodo.' });
    if (data.open)
      insights.push({
        level: 'advertencia',
        text: `${plural(data.open, 'alerta sigue abierta', 'alertas siguen abiertas')}.`,
      });
    if (topBranch && data.byBranch.length > 1)
      insights.push({
        level: 'info',
        text: `${topBranch.key} concentra el ${share(topBranch.count, data.total)}% de las alertas.`,
      });
    if (topType)
      insights.push({ level: 'info', text: `«${humanize(topType.key)}» es el tipo más frecuente (${topType.count}).` });
    if (topMachine && topMachine.count >= 3)
      insights.push({
        level: topMachine.critical ? 'critico' : 'advertencia',
        text: `${topMachine.name} acumuló ${topMachine.count} alertas.`,
      });
    if (busiest && data.perDay.length > 1)
      insights.push({
        level: 'info',
        text: `El día con más alertas fue el ${day(busiest.date)} (${busiest.critical + busiest.warning}).`,
      });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi ctx={ctx} id="alertas" value={data.total} />
          <Kpi ctx={ctx} id="alertas_criticas" value={data.critical} />
          <Kpi ctx={ctx} label="Siguen abiertas" value={data.open} tone={data.open > 0 ? 'advertencia' : 'neutral'} />
          <Kpi
            ctx={ctx}
            id="resolucion_alertas"
            label="Tiempo medio de resolución"
            value={data.averageResolutionHours}
            note={data.averageResolutionHours === null ? 'Ninguna resuelta todavía' : undefined}
          />
        </div>
        <Insights items={insights} />
        {data.perDay.length > 0 && (
          <Card title="Alertas por día" className="report-main-chart">
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
            <BarList
              label="Alertas por tipo"
              total={data.total}
              items={data.byCode.map((c) => ({ ...c, key: humanize(c.key) }))}
            />
          </Card>
          <Card title="Por sucursal">
            <BarList label="Alertas por sucursal" total={data.total} items={data.byBranch} />
          </Card>
        </div>
        <ReportTable
          title="Equipos con más alertas"
          noun="equipos"
          empty="Sin alertas en el periodo"
          rows={data.topMachines}
          rowKey={(m) => m.id}
          initialSort={{ key: 'count', dir: 'desc' }}
          columns={[
            {
              key: 'name',
              label: 'Equipo',
              sort: (m) => m.name,
              render: (m) => <MachineLink id={m.id} name={m.name} ctx={ctx} />,
            },
            { key: 'branch', label: 'Sucursal', sort: (m) => m.branchName, render: (m) => m.branchName },
            {
              key: 'count',
              label: 'Alertas',
              numeric: true,
              sort: (m) => m.count,
              render: (m) => <span className="mono">{m.count}</span>,
            },
            {
              key: 'critical',
              label: 'Críticas',
              numeric: true,
              sort: (m) => m.critical,
              render: (m) => <span className={`mono${m.critical ? ' report-critical' : ''}`}>{m.critical}</span>,
            },
          ]}
        />
      </>
    );
  },
  csv: (data) => ({
    columns: ['Día', 'Críticas', 'Advertencias'],
    rows: data.perDay.map((d) => [d.date, d.critical, d.warning]),
  }),
});

// ------------------------------------------------------------------ discos y seguridad

interface DiskIssueRow {
  key: string;
  machineId: number;
  machineName: string;
  branchName: string;
  level: 'critico' | 'advertencia';
  problem: string;
  detail: string;
  /** Para ordenar los discos llenos por espacio libre. */
  freePercent: number | null;
}

const diskSecurity = define<DiskSecurityReport>({
  id: 'discos-seguridad',
  title: 'Discos y seguridad',
  short: 'Discos y seguridad',
  description: 'Discos casi llenos o con fallas, y equipos sin antivirus o con el firewall apagado.',
  icon: 'shield',
  advanced: false,
  period: false,
  load: (api, f) => api.reportes.diskSecurity(f),
  render: (data, ctx) => {
    const rows: DiskIssueRow[] = [
      ...data.diskHealth.map((d, i) => ({
        key: `h${i}`,
        machineId: d.machineId,
        machineName: d.machineName,
        branchName: d.branchName,
        level: (d.health === 'critico' ? 'critico' : 'advertencia') as DiskIssueRow['level'],
        problem: 'Disco con fallas',
        detail: [d.disk, d.detail ?? STATUS_LABELS[d.health as MachineStatus]].filter(Boolean).join(' · '),
        freePercent: null,
      })),
      ...data.security.map((s, i) => ({
        key: `s${i}`,
        machineId: s.machineId,
        machineName: s.machineName,
        branchName: s.branchName,
        level: (s.issue === 'antivirus_desactualizado' ? 'advertencia' : 'critico') as DiskIssueRow['level'],
        problem: ISSUE_LABEL[s.issue],
        detail: s.detail ?? '',
        freePercent: null,
      })),
      ...data.lowDisks.map((d) => ({
        key: `l${d.machineId}-${d.drive}`,
        machineId: d.machineId,
        machineName: d.machineName,
        branchName: d.branchName,
        level: (d.freePercent < 5 ? 'critico' : 'advertencia') as DiskIssueRow['level'],
        problem: `Disco ${d.drive} casi lleno`,
        detail: `${num(d.freeGb)} de ${num(d.totalGb)} GB libres (${num(d.freePercent)}%)`,
        freePercent: d.freePercent,
      })),
    ];
    const unprotected = new Set(
      data.security.filter((s) => s.issue !== 'antivirus_desactualizado').map((s) => s.machineId),
    ).size;
    const critical = rows.filter((r) => r.level === 'critico').length;
    const insights: Insight[] = [];
    if (unprotected)
      insights.push({
        level: 'critico',
        text: `${plural(unprotected, 'equipo está', 'equipos están')} sin antivirus activo o con el firewall apagado.`,
      });
    if (data.diskHealth.length)
      insights.push({
        level: 'critico',
        text: `${plural(data.diskHealth.length, 'disco reporta', 'discos reportan')} fallas: conviene respaldar y reemplazar.`,
      });
    const nearlyFull = data.lowDisks.filter((d) => d.freePercent < 5).length;
    if (nearlyFull)
      insights.push({
        level: 'advertencia',
        text: `${plural(nearlyFull, 'disco tiene', 'discos tienen')} menos de 5% libre.`,
      });
    if (!rows.length) insights.push({ level: 'sano', text: 'Ningún disco en riesgo y todos los equipos protegidos.' });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi
            ctx={ctx}
            label="Discos con menos de 15% libre"
            value={data.lowDisks.length}
            tone={data.lowDisks.length ? 'advertencia' : 'sano'}
          />
          <Kpi
            ctx={ctx}
            label="Discos con fallas"
            value={data.diskHealth.length}
            tone={data.diskHealth.length ? 'critico' : 'sano'}
          />
          <Kpi ctx={ctx} id="seguridad" value={new Set(data.security.map((s) => s.machineId)).size} trend={false} />
          <Kpi ctx={ctx} label="Problemas críticos" value={critical} tone={critical ? 'critico' : 'sano'} />
        </div>
        <Insights items={insights} />
        <ReportTable
          title="Problemas encontrados"
          noun="problemas"
          empty="Ningún disco en riesgo y todos los equipos protegidos"
          rows={rows}
          rowKey={(r) => r.key}
          initialSort={{ key: 'level', dir: 'asc' }}
          columns={[
            {
              key: 'level',
              label: 'Nivel',
              sort: (r) => (r.level === 'critico' ? 0 : 1),
              render: (r) => (
                <span className="report-level mono" data-level={r.level}>
                  {r.level === 'critico' ? 'CRIT' : 'ADV'}
                </span>
              ),
            },
            {
              key: 'name',
              label: 'Equipo',
              sort: (r) => r.machineName,
              render: (r) => <MachineLink id={r.machineId} name={r.machineName} ctx={ctx} />,
            },
            { key: 'branch', label: 'Sucursal', sort: (r) => r.branchName, render: (r) => r.branchName },
            { key: 'problem', label: 'Problema', sort: (r) => r.problem, render: (r) => r.problem },
            {
              key: 'detail',
              label: 'Detalle',
              sort: (r) => r.freePercent ?? r.detail,
              render: (r) =>
                r.freePercent !== null ? (
                  <span className="report-detail-bar">
                    <PercentBar value={100 - r.freePercent} level={r.level} />
                    <span className="muted">{r.detail}</span>
                  </span>
                ) : (
                  <span className="muted">{r.detail || '—'}</span>
                ),
            },
          ]}
        />
      </>
    );
  },
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
  short: 'Rendimiento',
  description: 'Uso de CPU, memoria y disco por equipo, y cuáles conviene ampliar o cambiar.',
  icon: 'cpu',
  advanced: true,
  period: true,
  load: (api, f) => api.reportes.performance(f),
  render: (data, ctx) => {
    const flagged = data.machines.filter((m) => m.advice.length > 0).length;
    const avg = (values: (number | null)[]) => {
      const list = values.filter((v): v is number => v !== null);
      return list.length ? Math.round((list.reduce((a, b) => a + b, 0) / list.length) * 10) / 10 : null;
    };
    const ram = data.machines.filter((m) => m.advice.includes('ampliar_ram')).length;
    const cpu = data.machines.filter((m) => m.advice.includes('revisar_cpu')).length;
    const disk = data.machines.filter((m) => m.advice.includes('liberar_disco')).length;
    const insights: Insight[] = [];
    if (ram)
      insights.push({
        level: 'advertencia',
        text: `${plural(ram, 'equipo usa', 'equipos usan')} en promedio 85% o más de la RAM: conviene ampliarla.`,
      });
    if (cpu)
      insights.push({
        level: 'advertencia',
        text: `${plural(cpu, 'equipo tiene', 'equipos tienen')} el procesador al 80% o más en promedio.`,
      });
    if (disk)
      insights.push({
        level: 'advertencia',
        text: `${plural(disk, 'equipo llegó', 'equipos llegaron')} al 90% del disco.`,
      });
    if (data.machines.length && !flagged)
      insights.push({ level: 'sano', text: 'Ningún equipo necesita ampliarse por ahora.' });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi ctx={ctx} id="cpu" value={avg(data.trend.map((t) => t.cpu))} />
          <Kpi ctx={ctx} id="ram" value={avg(data.trend.map((t) => t.ram))} />
          <Kpi ctx={ctx} label="Equipos medidos" value={data.machines.length} />
          <Kpi ctx={ctx} label="Conviene revisar" value={flagged} tone={flagged > 0 ? 'advertencia' : 'sano'} />
        </div>
        <Insights items={insights} />
        {data.trend.length > 1 && (
          <Card title="Uso promedio de la flota" className="report-main-chart">
            <LineChart
              xLabels={data.trend.map((d) => dayLabel(d.date))}
              pointLabels={data.trend.map((d) => d.date)}
              threshold={85}
              series={[
                { name: 'CPU', color: 'cpu', values: data.trend.map((d) => d.cpu) },
                { name: 'RAM', color: 'ram', values: data.trend.map((d) => d.ram), area: true },
              ]}
            />
          </Card>
        )}
        <ReportTable
          title="Equipos"
          noun="equipos"
          empty="No hay métricas en el periodo"
          rows={data.machines}
          rowKey={(m) => m.id}
          problem={(m) => m.advice.length > 0}
          columns={[
            {
              key: 'name',
              label: 'Equipo',
              sort: (m) => m.name,
              render: (m) => <MachineLink id={m.id} name={m.name} ctx={ctx} />,
            },
            { key: 'branch', label: 'Sucursal', sort: (m) => m.branchName, render: (m) => m.branchName },
            {
              key: 'cpu',
              label: 'CPU prom.',
              sort: (m) => m.cpuAverage,
              render: (m) => <PercentBar value={m.cpuAverage} level={usageLevel(m.cpuAverage)} />,
            },
            {
              key: 'cpuPeak',
              label: 'CPU máx.',
              numeric: true,
              sort: (m) => m.cpuPeak,
              render: (m) => <span className="mono">{pct(m.cpuPeak)}</span>,
            },
            {
              key: 'ram',
              label: 'RAM prom.',
              sort: (m) => m.ramAverage,
              render: (m) => <PercentBar value={m.ramAverage} level={usageLevel(m.ramAverage)} />,
            },
            {
              key: 'ramPeak',
              label: 'RAM máx.',
              numeric: true,
              sort: (m) => m.ramPeak,
              render: (m) => <span className="mono">{pct(m.ramPeak)}</span>,
            },
            {
              key: 'disk',
              label: 'Disco máx.',
              numeric: true,
              sort: (m) => m.diskMax,
              render: (m) => <span className="mono">{pct(m.diskMax)}</span>,
            },
            {
              key: 'advice',
              label: 'Recomendación',
              sort: (m) => -m.advice.length,
              render: (m) =>
                m.advice.length === 0 ? (
                  <span className="muted">—</span>
                ) : (
                  m.advice.map((a) => (
                    <span key={a} className="report-chip" data-level="advertencia">
                      {ADVICE_LABEL[a]}
                    </span>
                  ))
                ),
            },
          ]}
        />
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
  short: 'Software',
  description: 'Cada programa con cuántos equipos lo tienen y en qué versiones. Busque uno para ver quién lo tiene.',
  icon: 'download',
  advanced: true,
  period: false,
  search: true,
  load: (api, f) => api.reportes.software(f),
  render: (data, ctx) => {
    const fragmented = data.rows.filter((r) => r.versions.length >= 3).length;
    const top = data.rows[0];
    const insights: Insight[] = [];
    if (top)
      insights.push({
        level: 'info',
        text: `${top.name} es el más extendido: está en ${plural(top.machines, 'equipo', 'equipos')}.`,
      });
    if (fragmented)
      insights.push({
        level: 'advertencia',
        text: `${plural(fragmented, 'programa tiene', 'programas tienen')} 3 versiones o más instaladas: conviene unificar.`,
      });
    return (
      <>
        <div className="report-kpi-grid">
          <Kpi ctx={ctx} label="Programas distintos" value={data.programs} note="Foto de hoy" />
          <Kpi
            ctx={ctx}
            label="Con 3 versiones o más"
            value={fragmented}
            tone={fragmented ? 'advertencia' : 'neutral'}
          />
          {data.holders.length > 0 && (
            <Kpi ctx={ctx} label="Equipos que lo tienen" value={new Set(data.holders.map((h) => h.machineId)).size} />
          )}
        </div>
        <Insights items={insights} />
        {data.holders.length > 0 && (
          <ReportTable
            title="Equipos que lo tienen"
            noun="instalaciones"
            empty="Ningún equipo lo tiene"
            rows={data.holders}
            rowKey={(h, i) => `${h.machineId}-${i}`}
            columns={[
              {
                key: 'name',
                label: 'Equipo',
                sort: (h) => h.machineName,
                render: (h) => <MachineLink id={h.machineId} name={h.machineName} ctx={ctx} />,
              },
              { key: 'branch', label: 'Sucursal', sort: (h) => h.branchName, render: (h) => h.branchName },
              {
                key: 'version',
                label: 'Versión',
                sort: (h) => h.version,
                render: (h) => <span className="mono">{h.version ?? '—'}</span>,
              },
              { key: 'installed', label: 'Instalado', sort: (h) => h.installedOn, render: (h) => day(h.installedOn) },
            ]}
          />
        )}
        <ReportTable
          title="Programas"
          noun="programas"
          empty="Ningún programa coincide"
          rows={data.rows}
          rowKey={(r) => r.name}
          problem={(r) => r.versions.length >= 3}
          columns={[
            { key: 'name', label: 'Programa', sort: (r) => r.name, render: (r) => r.name },
            { key: 'publisher', label: 'Editor', sort: (r) => r.publisher, render: (r) => r.publisher ?? '—' },
            {
              key: 'machines',
              label: 'Equipos',
              numeric: true,
              sort: (r) => r.machines,
              render: (r) => <span className="mono">{r.machines}</span>,
            },
            {
              key: 'versions',
              label: 'Versiones',
              sort: (r) => r.versions.length,
              render: (r) => (
                <>
                  {r.versions.slice(0, 4).map((v) => (
                    <span key={v.key} className="report-chip mono">
                      {v.key} · {v.count}
                    </span>
                  ))}
                  {r.versions.length > 4 && <span className="muted"> +{r.versions.length - 4}</span>}
                </>
              ),
            },
          ]}
        />
      </>
    );
  },
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
  short: 'Tickets',
  description: 'Tickets abiertos y cerrados, tiempo de solución, carga por técnico y mantenimientos al día.',
  icon: 'ticket',
  advanced: true,
  period: true,
  load: (api, f) => api.reportes.tickets(f),
  render: (data, ctx) => {
    const top = data.technicians[0];
    const totalAssigned = data.technicians.reduce((s, t) => s + t.assigned, 0);
    const insights: Insight[] = [];
    if (data.maintenance.overdue)
      insights.push({
        level: 'critico',
        text: `${plural(data.maintenance.overdue, 'mantenimiento está vencido', 'mantenimientos están vencidos')}.`,
      });
    if (data.created > data.closed)
      insights.push({
        level: 'advertencia',
        text: `Entraron ${data.created - data.closed} tickets más de los que se cerraron: la cola crece.`,
      });
    if (top && data.technicians.length > 1 && share(top.assigned, totalAssigned) >= 50)
      insights.push({
        level: 'advertencia',
        text: `${top.name} lleva el ${share(top.assigned, totalAssigned)}% de los tickets asignados.`,
      });
    if (data.created && data.created <= data.closed)
      insights.push({ level: 'sano', text: 'Se cerraron tantos tickets como entraron.' });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi ctx={ctx} id="tickets_creados" label="Creados" value={data.created} />
          <Kpi ctx={ctx} label="Cerrados" value={data.closed} tone="sano" />
          <Kpi ctx={ctx} id="tickets_abiertos" label="Abiertos hoy" value={data.open} />
          <Kpi
            ctx={ctx}
            id="resolucion_tickets"
            label="Tiempo medio de solución"
            value={data.averageResolutionHours}
            note={data.averageResolutionHours === null ? 'Ninguno cerrado en el periodo' : undefined}
          />
        </div>
        <Insights items={insights} />
        <div className="report-two">
          <Card title="Por prioridad">
            <BarList
              label="Tickets por prioridad"
              total={data.created}
              items={data.byPriority.map((p) => ({ ...p, key: PRIORITY_LABEL[p.key as TicketPriority] ?? p.key }))}
            />
          </Card>
          <Card title="Mantenimientos del periodo">
            <div className="report-kpi-grid report-kpi-grid-3">
              <DeltaKpi label="Hechos" value={data.maintenance.done} tone="sano" />
              <DeltaKpi label="Pendientes" value={data.maintenance.pending} />
              <DeltaKpi
                label="Vencidos"
                value={data.maintenance.overdue}
                tone={data.maintenance.overdue > 0 ? 'critico' : 'neutral'}
              />
            </div>
          </Card>
        </div>
        <ReportTable
          title="Carga por técnico"
          noun="técnicos"
          empty="Ningún ticket asignado en el periodo"
          rows={data.technicians}
          rowKey={(t) => t.name}
          initialSort={{ key: 'assigned', dir: 'desc' }}
          columns={[
            { key: 'name', label: 'Técnico', sort: (t) => t.name, render: (t) => t.name },
            {
              key: 'assigned',
              label: 'Asignados',
              numeric: true,
              sort: (t) => t.assigned,
              render: (t) => <span className="mono">{t.assigned}</span>,
            },
            {
              key: 'closed',
              label: 'Cerrados',
              numeric: true,
              sort: (t) => t.closed,
              render: (t) => <span className="mono">{t.closed}</span>,
            },
            {
              key: 'rate',
              label: 'Cerrados (%)',
              sort: (t) => share(t.closed, t.assigned),
              render: (t) => (
                <PercentBar
                  value={share(t.closed, t.assigned)}
                  level={share(t.closed, t.assigned) >= 80 ? 'sano' : 'advertencia'}
                />
              ),
            },
            {
              key: 'time',
              label: 'Tiempo medio',
              numeric: true,
              sort: (t) => t.averageResolutionHours,
              render: (t) => <span className="mono">{hours(t.averageResolutionHours)}</span>,
            },
          ]}
        />
      </>
    );
  },
  csv: (data) => ({
    columns: ['Técnico', 'Asignados', 'Cerrados', 'Tiempo medio (h)'],
    rows: data.technicians.map((t) => [t.name, t.assigned, t.closed, t.averageResolutionHours]),
  }),
});

// ------------------------------------------------------------------ accesos remotos (avanzado)

const remote = define<RemoteReport>({
  id: 'remoto',
  title: 'Accesos remotos',
  short: 'Accesos remotos',
  description: 'Quién se conectó a qué equipo, cuánto tiempo, en qué modo y por qué.',
  icon: 'remote',
  advanced: true,
  period: true,
  load: (api, f) => api.reportes.remote(f),
  render: (data, ctx) => {
    const noReason = data.rows.filter((s) => !s.reason).length;
    const byMachine = Object.values(
      data.rows.reduce<Record<number, { name: string; count: number }>>(
        (acc, s) => ({ ...acc, [s.machineId]: { name: s.machineName, count: (acc[s.machineId]?.count ?? 0) + 1 } }),
        {},
      ),
    ).sort((a, b) => b.count - a.count);
    const insights: Insight[] = [];
    if (noReason)
      insights.push({
        level: 'advertencia',
        text: `${plural(noReason, 'sesión no tiene', 'sesiones no tienen')} motivo registrado.`,
      });
    if (byMachine[0] && byMachine[0].count >= 3)
      insights.push({
        level: 'info',
        text: `${byMachine[0].name} es el equipo con más conexiones (${byMachine[0].count}).`,
      });
    if (data.technicians[0])
      insights.push({
        level: 'info',
        text: `${data.technicians[0].name} hizo ${plural(data.technicians[0].sessions, 'sesión', 'sesiones')}.`,
      });

    return (
      <>
        <div className="report-kpi-grid">
          <Kpi ctx={ctx} id="sesiones_remotas" label="Sesiones" value={data.sessions} />
          <Kpi
            ctx={ctx}
            id="horas_remotas"
            label="Tiempo conectado"
            value={Math.round((data.minutes / 60) * 10) / 10}
          />
          <Kpi ctx={ctx} label="Técnicos" value={data.technicians.length} />
          <Kpi ctx={ctx} label="Sin motivo" value={noReason} tone={noReason ? 'advertencia' : 'neutral'} />
        </div>
        <Insights items={insights} />
        <div className="report-two">
          <Card title="Sesiones por técnico">
            <BarList
              label="Sesiones por técnico"
              total={data.sessions}
              items={data.technicians.map((t) => ({ key: t.name, count: t.sessions }))}
            />
          </Card>
          <Card title="Equipos más atendidos">
            <BarList
              label="Sesiones por equipo"
              total={data.sessions}
              items={byMachine.slice(0, 6).map((m) => ({ key: m.name, count: m.count }))}
            />
          </Card>
        </div>
        <ReportTable
          title="Sesiones"
          noun="sesiones"
          empty="No hubo sesiones remotas en el periodo"
          rows={data.rows}
          rowKey={(s) => s.id}
          problem={(s) => !s.reason}
          columns={[
            { key: 'start', label: 'Inicio', sort: (s) => s.startedAt, render: (s) => dateTime(s.startedAt) },
            { key: 'user', label: 'Técnico', sort: (s) => s.userName, render: (s) => s.userName },
            {
              key: 'machine',
              label: 'Equipo',
              sort: (s) => s.machineName,
              render: (s) => (
                <>
                  <MachineLink id={s.machineId} name={s.machineName} ctx={ctx} />
                  <span className="muted report-sub">{s.branchName}</span>
                </>
              ),
            },
            {
              key: 'type',
              label: 'Tipo',
              sort: (s) => s.type,
              render: (s) => (
                <>
                  {SESSION_LABEL[s.type]}
                  {s.viewOnly && <span className="muted"> · solo ver</span>}
                </>
              ),
            },
            {
              key: 'minutes',
              label: 'Duración',
              numeric: true,
              sort: (s) => s.minutes,
              render: (s) => <span className="mono">{s.endedAt ? minutes(s.minutes) : 'En curso'}</span>,
            },
            {
              key: 'reason',
              label: 'Motivo',
              sort: (s) => s.reason,
              render: (s) => s.reason ?? <span className="muted">—</span>,
            },
          ]}
        />
      </>
    );
  },
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

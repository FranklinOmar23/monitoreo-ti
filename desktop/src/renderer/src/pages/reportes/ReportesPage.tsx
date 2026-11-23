import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ReportFilter, ReportSummary } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { usePanelApi } from '../../data/PanelData';
import { useDebounced } from '../../data/useDebounced';
import { csvFileName, downloadCsv, type CsvTable } from './csv';
import { REPORTS, type ReportId } from './reportDefs';
import { findKpi, KPI_META } from './summaryLabels';
import { SummaryView } from './SummaryView';

interface ReportesPageProps {
  onOpenEquipo: (id: number) => void;
  initialReport?: ReportId;
  /** Para el encabezado de la hoja impresa. */
  companyName?: string;
  logo?: string | null;
}

type Selected = 'resumen' | ReportId;
type Period = '7' | '30' | '90' | 'custom';

const iso = (date: Date) =>
  `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;

/** Fechas del periodo: los últimos N días con hoy incluido. */
export function periodRange(
  period: Exclude<Period, 'custom'>,
  today: Date = new Date(),
): { desde: string; hasta: string } {
  const from = new Date(today);
  from.setDate(from.getDate() - (Number(period) - 1));
  return { desde: iso(from), hasta: iso(today) };
}

/** CSV del resumen: cada indicador con su valor y el del periodo anterior. */
function summaryCsv(data: ReportSummary): CsvTable {
  return {
    columns: ['Indicador', 'Valor', 'Periodo anterior', 'Unidad'],
    rows: data.kpis.map((k) => [KPI_META[k.key].label, k.value, k.previous, KPI_META[k.key].unit ?? '']),
  };
}

/**
 * W-35 Reportes (decisión 92): menú lateral con el Resumen como portada y los reportes básicos y avanzados. El periodo,
 * la sucursal y el departamento se quedan al cambiar de reporte; cada uno se exporta a CSV o se imprime como PDF.
 */
export function ReportesPage({ onOpenEquipo, initialReport, companyName, logo }: ReportesPageProps) {
  const api = usePanelApi();
  const info = useQuery({ queryKey: ['reportes', 'info'], queryFn: () => api.reportes.info() });
  const advanced = info.data?.advanced ?? false;
  const [selected, setSelected] = useState<Selected>(initialReport ?? 'resumen');
  const report = REPORTS.find((r) => r.id === selected);
  const locked = Boolean(report?.advanced && !info.isPending && !advanced);

  const [period, setPeriod] = useState<Period>('30');
  const [custom, setCustom] = useState(() => periodRange('30'));
  const [branch, setBranch] = useState('');
  const [department, setDepartment] = useState('');
  const [search, setSearch] = useState('');
  const term = useDebounced(search.trim(), 300);

  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const departments = useQuery({ queryKey: ['departamentos'], queryFn: () => api.departments() });

  const range = period === 'custom' ? custom : periodRange(period);
  const validRange = Boolean(range.desde && range.hasta && range.desde <= range.hasta);
  const usesPeriod = selected === 'resumen' || Boolean(report?.period);
  const common: ReportFilter = {
    sucursal: branch ? Number(branch) : undefined,
    departamento: department ? Number(department) : undefined,
  };
  const periodFilter: ReportFilter = { ...range, ...common };

  // El resumen es la portada y, en cada reporte, da la variación frente al periodo anterior.
  const summary = useQuery({
    queryKey: ['reporte', 'resumen', periodFilter],
    queryFn: () => api.reportes.summary(periodFilter),
    enabled: validRange,
    placeholderData: keepPreviousData,
  });

  const filter: ReportFilter & { buscar?: string } = {
    ...(report?.period ? range : {}),
    ...common,
    buscar: report?.search && term ? term : undefined,
  };
  const data = useQuery({
    queryKey: ['reporte', report?.id, filter],
    queryFn: () => report!.load(api, filter),
    enabled: Boolean(report) && !locked && (!report?.period || validRange),
    // Mientras cambian los filtros se ve el dato anterior, pero solo del mismo reporte: los datos de otro reporte
    // tienen otra forma y romperían su render.
    placeholderData: (previous, previousQuery) => (previousQuery?.queryKey[1] === report?.id ? previous : undefined),
  });

  const open = (id: string) => {
    const target = REPORTS.find((r) => r.id === id);
    if (target && (!target.advanced || advanced)) setSelected(target.id);
  };

  const title = report?.title ?? 'Resumen';
  const description =
    report?.description ??
    'Los indicadores de todos los reportes frente al periodo anterior y lo que requiere atención.';
  const current = selected === 'resumen' ? summary : data;
  const csv = (): CsvTable | null =>
    selected === 'resumen'
      ? summary.data
        ? summaryCsv(summary.data)
        : null
      : data.data && report
        ? report.csv(data.data)
        : null;
  const periodText = usesPeriod ? `Del ${dayText(range.desde)} al ${dayText(range.hasta)}` : 'Estado actual';

  return (
    <div className="reports">
      <nav className="reports-nav no-print" aria-label="Reportes">
        <h1 className="reports-nav-title">Reportes</h1>
        <NavItem
          icon="dashboard"
          label="Resumen"
          active={selected === 'resumen'}
          onClick={() => setSelected('resumen')}
        />
        {(['Básicos', 'Avanzados'] as const).map((group) => (
          <div key={group} className="reports-nav-group" role="group" aria-label={`Reportes ${group.toLowerCase()}`}>
            <h2 className="eyebrow">{group}</h2>
            {REPORTS.filter((r) => r.advanced === (group === 'Avanzados')).map((r) => {
              const isLocked = r.advanced && !info.isPending && !advanced;
              const kpi = findKpi(
                summary.data?.kpis,
                r.id === 'discos-seguridad' ? 'discos_riesgo' : r.id === 'alertas' ? 'alertas_criticas' : 'equipos',
              );
              const badge =
                !isLocked && (r.id === 'alertas' || r.id === 'discos-seguridad') && kpi?.value ? kpi.value : null;
              return (
                <NavItem
                  key={r.id}
                  icon={isLocked ? 'lock' : r.icon}
                  label={r.short}
                  active={selected === r.id}
                  locked={isLocked}
                  badge={badge}
                  onClick={() => setSelected(r.id)}
                />
              );
            })}
          </div>
        ))}
        {!info.isPending && !advanced && (
          <p className="reports-nav-lock">Los avanzados están en el plan Empresarial.</p>
        )}
      </nav>

      <div className="reports-main">
        <div className="reports-toolbar no-print" role="group" aria-label="Filtros del reporte">
          {usesPeriod ? (
            <>
              <div className="segmented" role="group" aria-label="Periodo">
                {(['7', '30', '90'] as const).map((p) => (
                  <button key={p} type="button" aria-pressed={period === p} onClick={() => setPeriod(p)}>
                    {p} días
                  </button>
                ))}
                <button type="button" aria-pressed={period === 'custom'} onClick={() => setPeriod('custom')}>
                  Fechas
                </button>
              </div>
              {period === 'custom' && (
                <span className="report-dates">
                  <input
                    type="date"
                    aria-label="Desde"
                    value={custom.desde}
                    max={custom.hasta}
                    onChange={(e) => setCustom({ ...custom, desde: e.target.value })}
                  />
                  <span className="muted">a</span>
                  <input
                    type="date"
                    aria-label="Hasta"
                    value={custom.hasta}
                    min={custom.desde}
                    onChange={(e) => setCustom({ ...custom, hasta: e.target.value })}
                  />
                </span>
              )}
            </>
          ) : (
            <span
              className="report-snapshot mono"
              title="Este reporte muestra cómo están los equipos ahora; no depende del periodo."
            >
              <Icon name="clock" size={14} /> FOTO DE HOY
            </span>
          )}
          <FilterSelect
            label="Sucursal"
            value={branch}
            allLabel="Todas las sucursales"
            options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
            onChange={setBranch}
          />
          <FilterSelect
            label="Departamento"
            value={department}
            allLabel="Todos los departamentos"
            options={(departments.data ?? []).map((d) => ({ value: String(d.id), label: d.nombre }))}
            onChange={setDepartment}
          />
          {report?.search && (
            <label className="report-search">
              <Icon name="search" size={16} />
              <input
                type="search"
                placeholder="Buscar un programa…"
                aria-label="Buscar un programa"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </label>
          )}
          <span className="reports-toolbar-spacer" />
          <button
            type="button"
            className="btn btn-outline"
            disabled={!current.data || locked}
            onClick={() => {
              const table = csv();
              if (table) downloadCsv(table, csvFileName(selected));
            }}
          >
            <Icon name="download" size={16} /> Exportar CSV
          </button>
          <button
            type="button"
            className="btn btn-primary"
            disabled={!current.data || locked}
            onClick={() => window.print()}
          >
            Imprimir / PDF
          </button>
        </div>

        <header className="reports-head no-print">
          <h1>{title}</h1>
          <p className="muted">
            {description} <span className="mono reports-period">{periodText}</span>
          </p>
        </header>

        {/* Encabezado de la hoja impresa: el logo y el color de la empresa. */}
        <header className="print-only report-print-header">
          {logo && <img src={logo} alt="" />}
          <div>
            <strong>{companyName}</strong>
            <h1>{title}</h1>
            <span>
              {periodText} · Generado el {new Date().toLocaleString('es-DO', { dateStyle: 'long', timeStyle: 'short' })}
            </span>
          </div>
        </header>

        {locked ? (
          <div className="report-locked report-locked-lg">
            <Icon name="lock" size={20} />
            <span>
              <strong>{report?.title}</strong> es parte de los reportes avanzados, disponibles en el plan Empresarial.
            </span>
          </div>
        ) : usesPeriod && !validRange ? (
          <p className="auth-error">La fecha inicial debe ser anterior a la final.</p>
        ) : current.isPending ? (
          <Loading variant="table" columns={6} label="Preparando el reporte…" />
        ) : current.isError ? (
          <ErrorState error={current.error} onRetry={() => void current.refetch()} />
        ) : (
          <div className="report-body" data-refreshing={current.isFetching || undefined} key={selected}>
            {selected === 'resumen' ? (
              <SummaryView data={summary.data!} onOpenReport={open} onOpenEquipo={onOpenEquipo} />
            ) : (
              report!.render(data.data, { onOpenEquipo, summary: report!.period ? summary.data : undefined })
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function NavItem({
  icon,
  label,
  active,
  locked,
  badge,
  onClick,
}: {
  icon: Parameters<typeof Icon>[0]['name'];
  label: string;
  active: boolean;
  locked?: boolean;
  badge?: number | null;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      className="reports-nav-item"
      aria-current={active ? 'page' : undefined}
      data-locked={locked || undefined}
      title={locked ? 'Disponible en el plan Empresarial' : undefined}
      onClick={onClick}
    >
      <Icon name={icon} size={16} />
      <span>{label}</span>
      {badge ? <span className="reports-nav-badge mono">{badge}</span> : null}
    </button>
  );
}

function dayText(value: string): string {
  return value
    ? new Date(`${value}T12:00:00`).toLocaleDateString('es-DO', { day: 'numeric', month: 'long', year: 'numeric' })
    : '—';
}

import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ReportFilter } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { PageHeader } from '@components/PageHeader';
import { usePanelApi } from '../../data/PanelData';
import { useDebounced } from '../../data/useDebounced';
import { csvFileName, downloadCsv } from './csv';
import { REPORTS, type ReportDef, type ReportId } from './reportDefs';

interface ReportesPageProps {
  onOpenEquipo: (id: number) => void;
  initialReport?: ReportId;
  /** Para el encabezado de la hoja impresa. */
  companyName?: string;
  logo?: string | null;
}

/** W-35 Reportes: un catálogo de tarjetas; cada reporte con sus filtros, CSV e impresión. */
export function ReportesPage({ onOpenEquipo, initialReport, companyName, logo }: ReportesPageProps) {
  const api = usePanelApi();
  const info = useQuery({ queryKey: ['reportes', 'info'], queryFn: () => api.reportes.info() });
  const [selected, setSelected] = useState<ReportId | null>(initialReport ?? null);
  const report = REPORTS.find((r) => r.id === selected);

  if (report && (!report.advanced || info.data?.advanced))
    return (
      <ReportView
        report={report}
        onBack={() => setSelected(null)}
        onOpenEquipo={onOpenEquipo}
        companyName={companyName}
        logo={logo}
      />
    );

  const advanced = info.data?.advanced ?? false;
  return (
    <>
      <PageHeader title="Reportes" subtitle="Elija un reporte. Todos se pueden exportar a CSV o imprimir como PDF." />
      {info.isError && <ErrorState error={info.error} onRetry={() => void info.refetch()} />}
      <section aria-label="Reportes básicos">
        <h2 className="eyebrow report-group">Básicos</h2>
        <div className="report-grid">
          {REPORTS.filter((r) => !r.advanced).map((r) => (
            <ReportCard key={r.id} report={r} onOpen={() => setSelected(r.id)} />
          ))}
        </div>
      </section>
      <section aria-label="Reportes avanzados">
        <h2 className="eyebrow report-group">Avanzados</h2>
        <div className="report-grid">
          {REPORTS.filter((r) => r.advanced).map((r) => (
            <ReportCard key={r.id} report={r} locked={!info.isPending && !advanced} onOpen={() => setSelected(r.id)} />
          ))}
        </div>
      </section>
    </>
  );
}

function ReportCard({ report, locked, onOpen }: { report: ReportDef<unknown>; locked?: boolean; onOpen: () => void }) {
  return (
    <button
      type="button"
      className="report-card card"
      onClick={onOpen}
      disabled={locked}
      data-locked={locked || undefined}
    >
      <span className="report-card-icon" aria-hidden="true">
        <Icon name={locked ? 'lock' : report.icon} size={20} />
      </span>
      <strong>{report.title}</strong>
      <span className="muted">{report.description}</span>
      {locked && <span className="report-lock">Disponible en el plan Empresarial</span>}
    </button>
  );
}

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

function ReportView({
  report,
  onBack,
  onOpenEquipo,
  companyName,
  logo,
}: {
  report: ReportDef<unknown>;
  onBack: () => void;
  onOpenEquipo: (id: number) => void;
  companyName?: string;
  logo?: string | null;
}) {
  const api = usePanelApi();
  const [period, setPeriod] = useState<Period>('30');
  const [custom, setCustom] = useState(() => periodRange('30'));
  const [branch, setBranch] = useState('');
  const [department, setDepartment] = useState('');
  const [search, setSearch] = useState('');
  const term = useDebounced(search.trim(), 300);

  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const departments = useQuery({ queryKey: ['departamentos'], queryFn: () => api.departments() });

  const range = period === 'custom' ? custom : periodRange(period);
  const validRange = range.desde && range.hasta && range.desde <= range.hasta;
  const filter: ReportFilter & { buscar?: string } = {
    ...(report.period ? range : {}),
    sucursal: branch ? Number(branch) : undefined,
    departamento: department ? Number(department) : undefined,
    buscar: report.search && term ? term : undefined,
  };
  const data = useQuery({
    queryKey: ['reporte', report.id, filter],
    queryFn: () => report.load(api, filter),
    enabled: !report.period || Boolean(validRange),
    placeholderData: keepPreviousData,
  });

  const periodText = report.period ? `Del ${dayText(range.desde)} al ${dayText(range.hasta)}` : 'Estado actual';

  return (
    <div className="report-view">
      <div className="no-print">
        <PageHeader
          title={report.title}
          subtitle={report.description}
          actions={
            <>
              <button type="button" className="btn btn-outline" onClick={onBack}>
                Todos los reportes
              </button>
              <button
                type="button"
                className="btn btn-outline"
                disabled={!data.data}
                onClick={() => data.data && downloadCsv(report.csv(data.data), csvFileName(report.id))}
              >
                <Icon name="download" size={16} /> Exportar CSV
              </button>
              <button type="button" className="btn btn-primary" disabled={!data.data} onClick={() => window.print()}>
                Imprimir / PDF
              </button>
            </>
          }
        />
        <div className="report-filters" role="group" aria-label="Filtros del reporte">
          {report.period && (
            <>
              <div className="segmented segmented-lg" role="group" aria-label="Periodo">
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
                  <label>
                    <span className="visually-hidden">Desde</span>
                    <input
                      type="date"
                      aria-label="Desde"
                      value={custom.desde}
                      max={custom.hasta}
                      onChange={(e) => setCustom({ ...custom, desde: e.target.value })}
                    />
                  </label>
                  <span className="muted">a</span>
                  <label>
                    <span className="visually-hidden">Hasta</span>
                    <input
                      type="date"
                      aria-label="Hasta"
                      value={custom.hasta}
                      min={custom.desde}
                      onChange={(e) => setCustom({ ...custom, hasta: e.target.value })}
                    />
                  </label>
                </span>
              )}
            </>
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
          {report.search && (
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
        </div>
      </div>

      {/* Encabezado de la hoja impresa: el logo y el color de la empresa. */}
      <header className="print-only report-print-header">
        {logo && <img src={logo} alt="" />}
        <div>
          <strong>{companyName}</strong>
          <h1>{report.title}</h1>
          <span>
            {periodText} · Generado el {new Date().toLocaleString('es-DO', { dateStyle: 'long', timeStyle: 'short' })}
          </span>
        </div>
      </header>

      {report.period && !validRange ? (
        <p className="auth-error">La fecha inicial debe ser anterior a la final.</p>
      ) : data.isPending ? (
        <Loading variant="table" columns={6} label="Preparando el reporte…" />
      ) : data.isError ? (
        <ErrorState error={data.error} onRetry={() => void data.refetch()} />
      ) : (
        <div className="report-body" data-refreshing={data.isFetching || undefined}>
          {report.render(data.data, { onOpenEquipo })}
        </div>
      )}
    </div>
  );
}

function dayText(value: string): string {
  return value
    ? new Date(`${value}T12:00:00`).toLocaleDateString('es-DO', { day: 'numeric', month: 'long', year: 'numeric' })
    : '—';
}

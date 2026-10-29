import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { MachineListItem, MachineStatus } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { MiniBar } from '@components/MiniBar';
import { PageHeader } from '@components/PageHeader';
import { StatusBadge } from '@components/StatusBadge';
import { isLate, MACHINE_TYPES, timeAgo, typeIcon, typeLabel, whole } from '../data/format';
import { usePanelApi } from '../data/PanelData';
import { useDebounced } from '../data/useDebounced';

const ESTADOS: { value: MachineStatus; label: string }[] = [
  { value: 'sano', label: 'Sano' },
  { value: 'advertencia', label: 'Advertencia' },
  { value: 'critico', label: 'Crítico' },
  { value: 'desconectado', label: 'Desconectado' },
];

const PAGE_SIZE = 50;

interface EquiposPageProps {
  query: string;
  onQueryChange: (value: string) => void;
  onOpenEquipo: (id: number) => void;
  onAddMachine?: () => void;
  onPrintQr?: (ids: number[]) => void;
  /** Abre la lista ya filtrada por una sucursal (desde la pantalla de Sucursales). */
  initialBranchId?: number | null;
}

/** W-05 Lista de equipos. */
export function EquiposPage({
  query,
  onQueryChange,
  onOpenEquipo,
  onAddMachine,
  onPrintQr,
  initialBranchId,
}: EquiposPageProps) {
  const api = usePanelApi();
  const [sucursal, setSucursal] = useState(initialBranchId ? String(initialBranchId) : '');
  const [tipo, setTipo] = useState('');
  const [estado, setEstado] = useState('');
  const [sistema, setSistema] = useState('');
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set());
  const search = useDebounced(query.trim());

  const filters = { q: search, sucursal: sucursal ? Number(sucursal) : null, tipo, estado, so: sistema };
  // Un filtro nuevo vuelve a la primera página.
  const filterKey = JSON.stringify(filters);
  const [lastFilterKey, setLastFilterKey] = useState(filterKey);
  if (filterKey !== lastFilterKey) {
    setLastFilterKey(filterKey);
    setPage(1);
  }

  const list = useQuery({
    queryKey: ['maquinas', filters, page],
    queryFn: () => api.machines({ ...filters, pagina: page, tamano: PAGE_SIZE }),
    placeholderData: keepPreviousData,
    refetchInterval: 60_000,
  });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });

  const items = list.data?.page.items ?? [];
  const toggle = (id: number) =>
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  const allSelected = items.length > 0 && items.every((item) => selected.has(item.id));
  const toggleAll = () => setSelected(allSelected ? new Set() : new Set(items.map((item) => item.id)));

  const counts = list.data?.counts;
  const total = list.data?.page.total ?? 0;
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  const chip = (value: MachineStatus, label: string, count: number) => (
    <button
      type="button"
      className="chip"
      data-status={value}
      aria-pressed={estado === value}
      onClick={() => setEstado(estado === value ? '' : value)}
    >
      {count} {label}
    </button>
  );

  return (
    <>
      <PageHeader
        title="Equipos"
        subtitle={counts ? `${counts.total} equipos registrados · ${counts.online} en línea` : 'Cargando…'}
        actions={
          onAddMachine && (
            <button type="button" className="btn btn-primary" onClick={onAddMachine}>
              Agregar equipo
            </button>
          )
        }
      />

      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="Filtrar por nombre, IP o MAC"
            aria-label="Filtrar por nombre, IP o MAC"
            onChange={(event) => onQueryChange(event.target.value)}
          />
        </label>
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
        <FilterSelect
          label="Tipo"
          value={tipo}
          options={MACHINE_TYPES.map((t) => ({ value: t, label: typeLabel(t) }))}
          allLabel="Todos"
          onChange={setTipo}
        />
        <FilterSelect label="Estado" value={estado} options={ESTADOS} allLabel="Todos" onChange={setEstado} />
        <FilterSelect
          label="Sistema operativo"
          value={sistema}
          options={(list.data?.operatingSystems ?? []).map((s) => ({ value: s, label: s }))}
          allLabel="Todos"
          onChange={setSistema}
        />
      </div>

      {counts && (
        <div className="chips">
          {chip('critico', 'críticos', counts.critical)}
          {chip('advertencia', 'advertencia', counts.warning)}
          {chip('desconectado', 'desconectados', counts.disconnected)}
        </div>
      )}

      {list.isPending ? (
        <Loading />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : (
        <div className="table-card">
          {selected.size > 0 && (
            <div className="bulk-bar" role="toolbar" aria-label="Acciones en lote">
              <strong>
                {selected.size} {selected.size === 1 ? 'equipo seleccionado' : 'equipos seleccionados'}
              </strong>
              <button type="button" className="bulk-action" disabled title="Disponible con la gestión remota (Fase 6)">
                <Icon name="terminal" size={16} /> Ejecutar script
              </button>
              {onPrintQr && (
                <button type="button" className="bulk-action" onClick={() => onPrintQr([...selected])}>
                  <Icon name="qr" size={16} /> Imprimir QR
                </button>
              )}
              <button type="button" className="bulk-action" disabled title="Disponible con la gestión remota (Fase 6)">
                <Icon name="refresh" size={16} /> Reiniciar
              </button>
              <button type="button" className="bulk-clear" onClick={() => setSelected(new Set())}>
                Quitar selección
              </button>
            </div>
          )}

          <table className="data-table">
            <thead>
              <tr>
                <th className="col-check">
                  <input type="checkbox" checked={allSelected} onChange={toggleAll} aria-label="Seleccionar todos" />
                </th>
                <th>Estado</th>
                <th>Nombre</th>
                <th>Sucursal</th>
                <th>Tipo</th>
                <th>IP</th>
                <th>CPU</th>
                <th>RAM</th>
                <th>Disco</th>
                <th>Último reporte</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <MachineRow
                  key={item.id}
                  item={item}
                  selected={selected.has(item.id)}
                  onToggle={() => toggle(item.id)}
                  onOpen={() => onOpenEquipo(item.id)}
                />
              ))}
            </tbody>
          </table>
          {items.length === 0 && (
            <EmptyState title="Ningún equipo coincide con los filtros.">
              Cambie los filtros o instale el agente en un equipo nuevo.
            </EmptyState>
          )}
          {pages > 1 && (
            <div className="pager">
              <span className="muted">
                Página {page} de {pages} · {total} equipos
              </span>
              <button type="button" className="btn btn-outline" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                Anterior
              </button>
              <button
                type="button"
                className="btn btn-outline"
                disabled={page >= pages}
                onClick={() => setPage(page + 1)}
              >
                Siguiente
              </button>
            </div>
          )}
        </div>
      )}
    </>
  );
}

interface MachineRowProps {
  item: MachineListItem;
  selected: boolean;
  onToggle: () => void;
  onOpen: () => void;
}

function MachineRow({ item, selected, onToggle, onOpen }: MachineRowProps) {
  const late = item.status !== 'desconectado' && isLate(item.lastReportAt);
  return (
    <tr className={selected ? 'is-selected' : undefined}>
      <td className="col-check">
        <input type="checkbox" checked={selected} onChange={onToggle} aria-label={`Seleccionar ${item.name}`} />
      </td>
      <td>
        <StatusBadge status={item.status} />
      </td>
      <td>
        <button type="button" className="row-name mono" onClick={onOpen}>
          {item.name}
        </button>
        <span className="row-sub">{item.detail ?? '—'}</span>
      </td>
      <td>{item.branchName}</td>
      <td>
        <span className="tipo">
          <Icon name={typeIcon(item.type)} size={16} />
          {typeLabel(item.type)}
        </span>
      </td>
      <td className="mono">{item.ipAddress ?? '—'}</td>
      <td>
        <MiniBar label={`CPU de ${item.name}`} percent={whole(item.metrics?.cpu)} />
      </td>
      <td>
        <MiniBar label={`RAM de ${item.name}`} percent={whole(item.metrics?.ram)} />
      </td>
      <td>
        <MiniBar label={`Disco de ${item.name}`} percent={whole(item.metrics?.disk)} />
      </td>
      <td className={`mono${late || item.status === 'desconectado' ? ' is-late' : ''}`}>
        {timeAgo(item.lastReportAt)}
      </td>
    </tr>
  );
}

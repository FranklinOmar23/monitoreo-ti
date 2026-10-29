import { useState, type DragEvent } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { TicketListItem, TicketPriority, TicketStatus } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { PageHeader } from '@components/PageHeader';
import { dateTime, timeAgo } from '../data/format';
import { usePanelApi } from '../data/PanelData';
import { useDebounced } from '../data/useDebounced';
import { NewTicketDialog } from './NewTicketDialog';
import { TicketDetailView } from './TicketDetail';
import { PRIORITY_LABEL, TICKET_STATUS_LABEL } from './ticketLabels';

/** Columnas del tablero; los cerrados se ven en la lista. */
const COLUMNS: TicketStatus[] = ['abierto', 'en_proceso', 'en_espera', 'resuelto'];

const ASSIGNEE_FILTERS = [
  { value: 'yo', label: 'Asignados a mí' },
  { value: 'sin', label: 'Sin asignar' },
];

const PRIORITY_FILTERS = (Object.keys(PRIORITY_LABEL) as TicketPriority[]).map((p) => ({
  value: p,
  label: PRIORITY_LABEL[p],
}));

const STATUS_FILTERS = [
  ...(Object.keys(TICKET_STATUS_LABEL) as TicketStatus[]).map((s) => ({ value: s, label: TICKET_STATUS_LABEL[s] })),
  { value: 'todos', label: 'Todos (con cerrados)' },
];

interface TicketsPageProps {
  canManage: boolean;
  onOpenEquipo: (id: number) => void;
  onOpenAlert: (id: number) => void;
  /** Abre directamente un ticket (recién creado desde la ficha o una alerta). */
  initialTicketId?: number | null;
}

/** W-24 Tablero, W-25 lista y W-26 detalle de tickets. */
export function TicketsPage({ canManage, onOpenEquipo, onOpenAlert, initialTicketId = null }: TicketsPageProps) {
  const [selected, setSelected] = useState<number | null>(initialTicketId);
  const [view, setView] = useState<'tablero' | 'lista'>('tablero');
  const [creating, setCreating] = useState(false);

  if (selected !== null)
    return (
      <TicketDetailView
        ticketId={selected}
        canManage={canManage}
        onBack={() => setSelected(null)}
        onOpenEquipo={onOpenEquipo}
        onOpenAlert={onOpenAlert}
      />
    );

  return (
    <>
      <PageHeader
        title="Tickets"
        subtitle="Trabajo de soporte sobre los equipos"
        actions={
          <>
            <div className="segmented" role="group" aria-label="Vista">
              <button type="button" aria-pressed={view === 'tablero'} onClick={() => setView('tablero')}>
                Tablero
              </button>
              <button type="button" aria-pressed={view === 'lista'} onClick={() => setView('lista')}>
                Lista
              </button>
            </div>
            <button type="button" className="btn btn-ticket" onClick={() => setCreating(true)}>
              <Icon name="plus" size={14} />
              Nuevo ticket
            </button>
          </>
        }
      />
      <TicketBrowser view={view} canManage={canManage} onOpen={setSelected} onOpenEquipo={onOpenEquipo} />
      {creating && (
        <NewTicketDialog
          canAssign={canManage}
          onClose={() => setCreating(false)}
          onCreated={(id) => {
            setCreating(false);
            setSelected(id);
          }}
        />
      )}
    </>
  );
}

function TicketBrowser({
  view,
  canManage,
  onOpen,
  onOpenEquipo,
}: {
  view: 'tablero' | 'lista';
  canManage: boolean;
  onOpen: (id: number) => void;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [query, setQuery] = useState('');
  const [asignado, setAsignado] = useState('');
  const [prioridad, setPrioridad] = useState('');
  const [estado, setEstado] = useState('');
  const [sucursal, setSucursal] = useState('');
  const search = useDebounced(query.trim());

  // El tablero muestra los activos; la lista puede incluir los cerrados.
  const filters = {
    q: search,
    asignado,
    prioridad,
    estado: view === 'lista' ? estado || 'activos' : 'activos',
    sucursal: sucursal ? Number(sucursal) : null,
    tamano: 300,
  };
  const list = useQuery({
    queryKey: ['tickets', filters],
    queryFn: () => api.tickets(filters),
    placeholderData: keepPreviousData,
    refetchInterval: 60_000,
  });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });

  const move = useMutation({
    mutationFn: ({ id, status }: { id: number; status: TicketStatus }) => api.setTicketStatus(id, status),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['tickets'] }),
  });

  const counts = list.data?.counts;
  const items = list.data?.page.items ?? [];

  return (
    <>
      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="Buscar por título, número o equipo"
            aria-label="Buscar ticket"
            onChange={(event) => setQuery(event.target.value)}
          />
        </label>
        <FilterSelect
          label="Asignados"
          value={asignado}
          options={ASSIGNEE_FILTERS}
          allLabel="Todos"
          onChange={setAsignado}
        />
        <FilterSelect
          label="Prioridad"
          value={prioridad}
          options={PRIORITY_FILTERS}
          allLabel="Todas"
          onChange={setPrioridad}
        />
        {view === 'lista' && (
          <FilterSelect
            label="Estado"
            value={estado}
            options={STATUS_FILTERS}
            allLabel="Activos"
            onChange={setEstado}
          />
        )}
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
      </div>

      {counts && canManage && (
        <div className="chips">
          <button
            type="button"
            className="chip"
            aria-pressed={asignado === 'yo'}
            onClick={() => setAsignado(asignado === 'yo' ? '' : 'yo')}
          >
            {counts.mine} asignados a mí
          </button>
          <button
            type="button"
            className="chip"
            data-status="advertencia"
            aria-pressed={asignado === 'sin'}
            onClick={() => setAsignado(asignado === 'sin' ? '' : 'sin')}
          >
            {counts.unassigned} sin asignar
          </button>
        </div>
      )}

      {move.isError && (
        <p className="auth-error" role="alert">
          {move.error.message}
        </p>
      )}

      {list.isPending ? (
        <Loading />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : view === 'tablero' ? (
        <Board items={items} canMove={canManage} onOpen={onOpen} onMove={(id, status) => move.mutate({ id, status })} />
      ) : (
        <TicketTable items={items} onOpen={onOpen} onOpenEquipo={onOpenEquipo} />
      )}
    </>
  );
}

function Board({
  items,
  canMove,
  onOpen,
  onMove,
}: {
  items: TicketListItem[];
  canMove: boolean;
  onOpen: (id: number) => void;
  onMove: (id: number, status: TicketStatus) => void;
}) {
  const [over, setOver] = useState<TicketStatus | null>(null);

  const drop = (status: TicketStatus) => (event: DragEvent) => {
    event.preventDefault();
    setOver(null);
    const id = Number(event.dataTransfer.getData('text/plain'));
    const ticket = items.find((t) => t.id === id);
    if (ticket && ticket.status !== status) onMove(id, status);
  };

  return (
    <div className="kanban">
      {COLUMNS.map((status) => {
        const column = items.filter((t) => t.status === status);
        return (
          <section
            key={status}
            className="kanban-col"
            data-over={over === status || undefined}
            aria-label={TICKET_STATUS_LABEL[status]}
            onDragOver={
              canMove
                ? (event) => {
                    event.preventDefault();
                    setOver(status);
                  }
                : undefined
            }
            onDragLeave={() => setOver(null)}
            onDrop={canMove ? drop(status) : undefined}
          >
            <header className="kanban-head">
              <span className="dot" data-ticket={status} aria-hidden="true" />
              <strong>{TICKET_STATUS_LABEL[status]}</strong>
              <span className="mono muted">{column.length}</span>
            </header>
            {column.length === 0 ? (
              <p className="muted kanban-empty">Sin tickets</p>
            ) : (
              column.map((ticket) => (
                <TicketCard
                  key={ticket.id}
                  ticket={ticket}
                  canMove={canMove}
                  onOpen={() => onOpen(ticket.id)}
                  onMove={onMove}
                />
              ))
            )}
          </section>
        );
      })}
    </div>
  );
}

function TicketCard({
  ticket,
  canMove,
  onOpen,
  onMove,
}: {
  ticket: TicketListItem;
  canMove: boolean;
  onOpen: () => void;
  onMove: (id: number, status: TicketStatus) => void;
}) {
  return (
    <article
      className="kanban-card"
      draggable={canMove}
      onDragStart={(event) => event.dataTransfer.setData('text/plain', String(ticket.id))}
    >
      <div className="kanban-card-top">
        <span className="mono muted">#{ticket.id}</span>
        <span className="priority" data-priority={ticket.priority}>
          {PRIORITY_LABEL[ticket.priority]}
        </span>
      </div>
      <button type="button" className="kanban-title" onClick={onOpen}>
        {ticket.title}
      </button>
      <span className="row-sub mono">{ticket.machineName ?? 'Sin equipo'}</span>
      <div className="kanban-card-foot">
        <span className="muted">
          <Icon name="user" size={12} /> {ticket.assignedTo ?? 'Sin asignar'}
        </span>
        <span className="muted">{timeAgo(ticket.createdAt)}</span>
      </div>
      {/* Mover sin arrastrar (teclado y lectores de pantalla). */}
      {canMove && (
        <select
          className="kanban-move"
          aria-label={`Mover el ticket ${ticket.id}`}
          value={ticket.status}
          onChange={(e) => onMove(ticket.id, e.target.value as TicketStatus)}
        >
          {(Object.keys(TICKET_STATUS_LABEL) as TicketStatus[]).map((s) => (
            <option key={s} value={s}>
              {TICKET_STATUS_LABEL[s]}
            </option>
          ))}
        </select>
      )}
    </article>
  );
}

function TicketTable({
  items,
  onOpen,
  onOpenEquipo,
}: {
  items: TicketListItem[];
  onOpen: (id: number) => void;
  onOpenEquipo: (id: number) => void;
}) {
  return (
    <div className="table-card">
      <table className="data-table">
        <thead>
          <tr>
            <th>#</th>
            <th>Ticket</th>
            <th>Prioridad</th>
            <th>Estado</th>
            <th>Equipo</th>
            <th>Asignado a</th>
            <th>Creado</th>
          </tr>
        </thead>
        <tbody>
          {items.map((t) => (
            <tr key={t.id} className={t.status === 'cerrado' ? 'is-muted' : undefined}>
              <td className="mono">{t.id}</td>
              <td>
                <button type="button" className="row-name" onClick={() => onOpen(t.id)}>
                  {t.title}
                </button>
                <span className="row-sub">
                  {t.createdBy}
                  {t.commentCount > 0 && ` · ${t.commentCount} comentarios`}
                </span>
              </td>
              <td>
                <span className="priority" data-priority={t.priority}>
                  {PRIORITY_LABEL[t.priority]}
                </span>
              </td>
              <td>
                <span className="ticket-status" data-ticket={t.status}>
                  {TICKET_STATUS_LABEL[t.status]}
                </span>
              </td>
              <td>
                {t.machineId !== null ? (
                  <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(t.machineId!)}>
                    {t.machineName}
                  </button>
                ) : (
                  '—'
                )}
              </td>
              <td>{t.assignedTo ?? '—'}</td>
              <td className="mono" title={dateTime(t.createdAt)}>
                {timeAgo(t.createdAt)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {items.length === 0 && <EmptyState title="Ningún ticket coincide con los filtros." />}
    </div>
  );
}

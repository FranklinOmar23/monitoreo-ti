import { useState, type FormEvent } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { AlertDetail, AlertLevel, AlertListItem, AlertStatus } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { countActive, MoreFilters } from '@components/MoreFilters';
import { Pager } from '@components/Pager';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { StatusBadge } from '@components/StatusBadge';
import { Tabs } from '@components/Tabs';
import { useLeaving } from '@components/useLeaving';
import { dateTime, timeAgo } from '../data/format';
import { usePanelApi } from '../data/PanelData';
import { useDebounced } from '../data/useDebounced';
import { ReglasPage } from './ReglasPage';
import { serviceFromAlertCode, StartServiceButton } from './remoto/StartServiceButton';

/** Decisión 83: 10 alertas por página. */
const PAGE_SIZE = 10;

const NO_EXTRA = { origen: '', tipo: '', desde: '', hasta: '' };

const ORIGINS = [
  { value: 'equipos', label: 'Equipos' },
  { value: 'red', label: 'Red' },
];

const KINDS = [
  { value: 'auto', label: 'Automáticas' },
  { value: 'manual', label: 'Creadas a mano' },
];

const STATUS_FILTERS = [
  { value: 'nueva', label: 'Nuevas' },
  { value: 'en_proceso', label: 'En proceso' },
  { value: 'resuelta', label: 'Resueltas' },
  { value: 'todas', label: 'Todas' },
];

const LEVELS = [
  { value: 'critico', label: 'Crítico' },
  { value: 'advertencia', label: 'Advertencia' },
];

const STATUS_LABEL: Record<AlertStatus, string> = { nueva: 'Nueva', en_proceso: 'En proceso', resuelta: 'Resuelta' };
const LEVEL_LABEL: Record<AlertLevel, string> = { critico: 'Crítico', advertencia: 'Advertencia' };

/** Enlace con los tickets, si el usuario puede verlos. */
export interface AlertTicketLinks {
  onCreate: (alert: AlertDetail) => void;
  onOpen: (ticketId: number) => void;
}

interface AlertasPageProps {
  canManage: boolean;
  /** scripts.ejecutar: puede iniciar el servicio de una alerta de servicio detenido. */
  canRunCommands?: boolean;
  onOpenEquipo: (id: number) => void;
  tickets?: AlertTicketLinks;
  /** Abre directamente una alerta (desde un ticket). */
  initialAlertId?: number | null;
}

/** W-20 Alertas, W-21 detalle y, en su pestaña, W-22/W-23 reglas. */
export function AlertasPage({
  canManage,
  canRunCommands = false,
  onOpenEquipo,
  tickets,
  initialAlertId = null,
}: AlertasPageProps) {
  const [tab, setTab] = useState('alertas');
  const [selected, setSelected] = useState<number | null>(initialAlertId);

  if (selected !== null)
    return (
      <AlertDetailView
        alertId={selected}
        canManage={canManage}
        canRunCommands={canRunCommands}
        tickets={tickets}
        onBack={() => setSelected(null)}
        onOpenEquipo={onOpenEquipo}
      />
    );

  return (
    <>
      <PageHeader title="Alertas" subtitle="Problemas detectados en los equipos y reglas que los disparan" />
      <Tabs
        items={[
          { id: 'alertas', label: 'Alertas' },
          { id: 'reglas', label: 'Reglas' },
        ]}
        activeId={tab}
        onChange={setTab}
        label="Secciones de alertas"
      />
      {tab === 'alertas' ? (
        <AlertList onOpen={setSelected} onOpenEquipo={onOpenEquipo} />
      ) : (
        <ReglasPage canManage={canManage} />
      )}
    </>
  );
}

function AlertList({ onOpen, onOpenEquipo }: { onOpen: (id: number) => void; onOpenEquipo: (id: number) => void }) {
  const api = usePanelApi();
  const [query, setQuery] = useState('');
  const [estado, setEstado] = useState('');
  const [nivel, setNivel] = useState('');
  const [sucursal, setSucursal] = useState('');
  const [page, setPage] = useState(1);
  const [extra, setExtra] = useState(NO_EXTRA);
  const search = useDebounced(query.trim());
  const datesValid = !(extra.desde && extra.hasta && extra.desde > extra.hasta);

  const filters = {
    q: search,
    estado: estado || 'abiertas',
    nivel,
    sucursal: sucursal ? Number(sucursal) : null,
    red: extra.origen === '' ? undefined : extra.origen === 'red',
    automatica: extra.tipo === '' ? undefined : extra.tipo === 'auto',
    fechaDesde: (datesValid && extra.desde) || undefined,
    fechaHasta: (datesValid && extra.hasta) || undefined,
  };
  const filterKey = JSON.stringify(filters);
  const [lastFilterKey, setLastFilterKey] = useState(filterKey);
  if (filterKey !== lastFilterKey) {
    setLastFilterKey(filterKey);
    setPage(1);
  }

  const list = useQuery({
    queryKey: ['alertas', filters, page],
    queryFn: () => api.alerts({ ...filters, page, limit: PAGE_SIZE }),
    placeholderData: keepPreviousData,
    refetchInterval: 60_000,
  });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });

  const counts = list.data?.counts;
  const items = list.data?.data.data ?? [];
  const rows = useLeaving(items, (alert) => alert.id);

  const chip = (label: string, count: number, status: string, onClick: () => void, pressed: boolean) => (
    <button type="button" className="chip" data-status={status} aria-pressed={pressed} onClick={onClick}>
      {count} {label}
    </button>
  );

  return (
    <>
      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="Buscar por equipo o mensaje"
            aria-label="Buscar alerta"
            onChange={(event) => setQuery(event.target.value)}
          />
        </label>
        <FilterSelect label="Estado" value={estado} options={STATUS_FILTERS} allLabel="Abiertas" onChange={setEstado} />
        <FilterSelect label="Nivel" value={nivel} options={LEVELS} allLabel="Todos" onChange={setNivel} />
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
        <MoreFilters
          active={countActive(...Object.values(extra).map((v) => v || null))}
          onClear={() => setExtra(NO_EXTRA)}
          dateLabel="Creada"
          from={extra.desde}
          to={extra.hasta}
          onDates={(desde, hasta) => setExtra({ ...extra, desde, hasta })}
        >
          <FilterSelect
            label="De"
            value={extra.origen}
            options={ORIGINS}
            allLabel="Equipos y red"
            onChange={(origen) => setExtra({ ...extra, origen })}
          />
          <FilterSelect
            label="Origen"
            value={extra.tipo}
            options={KINDS}
            allLabel="Todas"
            onChange={(tipo) => setExtra({ ...extra, tipo })}
          />
        </MoreFilters>
      </div>

      {counts && (
        <div className="chips">
          {chip(
            'críticas abiertas',
            counts.critical,
            'critico',
            () => setNivel(nivel === 'critico' ? '' : 'critico'),
            nivel === 'critico',
          )}
          {chip(
            'en advertencia',
            counts.warning,
            'advertencia',
            () => setNivel(nivel === 'advertencia' ? '' : 'advertencia'),
            nivel === 'advertencia',
          )}
          {chip(
            'sin atender',
            counts.new,
            'nueva',
            () => setEstado(estado === 'nueva' ? '' : 'nueva'),
            estado === 'nueva',
          )}
          {chip(
            'en proceso',
            counts.inProgress,
            'en_proceso',
            () => setEstado(estado === 'en_proceso' ? '' : 'en_proceso'),
            estado === 'en_proceso',
          )}
          {chip(
            'resueltas en 24 h',
            counts.resolvedLast24h,
            'sano',
            () => setEstado(estado === 'resuelta' ? '' : 'resuelta'),
            estado === 'resuelta',
          )}
        </div>
      )}

      {list.isPending ? (
        <Loading variant="table" columns={7} />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Nivel</th>
                <th>Alerta</th>
                <th>Equipo</th>
                <th>Sucursal</th>
                <th>Estado</th>
                <th>Atendida por</th>
                <th>Detectada</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ item: alert, leaving }) => (
                <AlertRow
                  key={alert.id}
                  alert={alert}
                  leaving={leaving}
                  onOpen={() => onOpen(alert.id)}
                  onOpenEquipo={onOpenEquipo}
                />
              ))}
            </tbody>
          </table>
          {items.length === 0 && (
            <EmptyState title={estado === '' ? 'No hay alertas abiertas' : 'Ninguna alerta coincide con los filtros'}>
              {estado === '' && 'Los equipos están dentro de los umbrales de sus reglas.'}
            </EmptyState>
          )}
          <Pager data={list.data?.data} noun="alertas" onPage={setPage} />
        </div>
      )}
    </>
  );
}

function AlertRow({
  alert,
  leaving,
  onOpen,
  onOpenEquipo,
}: {
  alert: AlertListItem;
  /** Ya resuelta o fuera del filtro: se desvanece antes de quitarse. */
  leaving?: boolean;
  onOpen: () => void;
  onOpenEquipo: (id: number) => void;
}) {
  const resolved = alert.status === 'resuelta';
  return (
    <tr className={resolved ? 'is-muted' : undefined} data-leaving={leaving || undefined}>
      <td>
        <StatusBadge status={resolved ? 'desconectado' : alert.level} label={LEVEL_LABEL[alert.level]} />
      </td>
      <td>
        <button type="button" className="row-name" onClick={onOpen}>
          {alert.message}
        </button>
        <span className="row-sub">{alert.source ?? (alert.automatic ? 'Motor de alertas' : 'Manual')}</span>
      </td>
      <td>
        {alert.machineId !== null ? (
          <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(alert.machineId!)}>
            {alert.machineName}
          </button>
        ) : (
          <span className="mono">{alert.deviceName ?? '—'}</span>
        )}
        {alert.machineId !== null && alert.deviceName && <span className="row-sub mono">{alert.deviceName}</span>}
      </td>
      <td>{alert.branchName ?? '—'}</td>
      <td>
        <span className="alert-status" data-status={alert.status}>
          {STATUS_LABEL[alert.status]}
        </span>
      </td>
      <td>{alert.handledBy ?? '—'}</td>
      <td className="mono" title={dateTime(alert.createdAt)}>
        {resolved && alert.resolvedAt ? `resuelta ${timeAgo(alert.resolvedAt)}` : timeAgo(alert.createdAt)}
      </td>
    </tr>
  );
}

// ------------------------------------------------------------------ W-21

function AlertDetailView({
  alertId,
  canManage,
  canRunCommands,
  tickets,
  onBack,
  onOpenEquipo,
}: {
  alertId: number;
  canManage: boolean;
  canRunCommands: boolean;
  tickets?: AlertTicketLinks;
  onBack: () => void;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const query = useQuery({ queryKey: ['alerta', alertId], queryFn: () => api.alert(alertId) });
  const [action, setAction] = useState<'atender' | 'resolver' | null>(null);

  return (
    <>
      <nav className="breadcrumb" aria-label="Ruta">
        <button type="button" className="link-quiet" onClick={onBack}>
          Alertas
        </button>
        <span aria-hidden="true">›</span>
        <strong className="mono">#{alertId}</strong>
      </nav>
      {query.isPending ? (
        <Loading variant="table" columns={5} />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <AlertDetailBody
          alert={query.data}
          canManage={canManage}
          canRunCommands={canRunCommands}
          tickets={tickets}
          onAction={setAction}
          onOpenEquipo={onOpenEquipo}
        />
      )}
      {action && query.data && <AlertActionDialog alert={query.data} action={action} onClose={() => setAction(null)} />}
    </>
  );
}

function AlertDetailBody({
  alert,
  canManage,
  canRunCommands,
  tickets,
  onAction,
  onOpenEquipo,
}: {
  alert: AlertDetail;
  canManage: boolean;
  canRunCommands: boolean;
  tickets?: AlertTicketLinks;
  onAction: (action: 'atender' | 'resolver') => void;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const linked = useQuery({
    queryKey: ['tickets', { alerta: alert.id }],
    queryFn: () => api.tickets({ alerta: alert.id, estado: 'todos', limit: 100 }),
    enabled: tickets !== undefined,
  });
  const linkedTickets = linked.data?.data.data ?? [];
  const hasActiveTicket = linkedTickets.some((t) => t.status !== 'resuelto' && t.status !== 'cerrado');
  const open = alert.status !== 'resuelta';
  return (
    <div className="ficha-grid">
      <div className="ficha-main">
        <Card>
          <div className="alert-head">
            <span className="problem-card-icon" data-status={open ? alert.level : 'sano'}>
              <Icon name={open ? (alert.level === 'critico' ? 'bolt' : 'bell') : 'check'} />
            </span>
            <div>
              <h1 className="alert-title">{alert.message}</h1>
              <div className="alert-meta">
                <StatusBadge status={open ? alert.level : 'desconectado'} label={LEVEL_LABEL[alert.level]} />
                <span className="alert-status" data-status={alert.status}>
                  {STATUS_LABEL[alert.status]}
                </span>
                <span className="muted">Detectada {dateTime(alert.createdAt)}</span>
              </div>
            </div>
            {open && (canManage || (tickets && !hasActiveTicket)) && (
              <div className="ficha-actions">
                {tickets && !hasActiveTicket && (
                  <button type="button" className="btn btn-ticket" onClick={() => tickets.onCreate(alert)}>
                    <Icon name="ticket" size={14} />
                    Crear ticket
                  </button>
                )}
                {canManage && alert.status === 'nueva' && (
                  <button type="button" className="btn btn-outline" onClick={() => onAction('atender')}>
                    Atender
                  </button>
                )}
                {canManage && (
                  <button type="button" className="btn btn-primary" onClick={() => onAction('resolver')}>
                    Resolver
                  </button>
                )}
              </div>
            )}
          </div>
          {alert.detail && <p className="alert-detail">{alert.detail}</p>}
          {alert.advice && (
            <div className="what-to-do">
              <Icon name="wrench" size={14} />
              <span>
                <strong>Qué hacer:</strong> {alert.advice}
              </span>
            </div>
          )}
          {alert.source && <span className="problem-origin mono">{alert.source}</span>}
          {canRunCommands && open && alert.machineId !== null && serviceFromAlertCode(alert.code) && (
            <StartServiceButton alertId={alert.id} service={serviceFromAlertCode(alert.code)!} />
          )}
          {alert.automatic && open && (
            <p className="muted note">
              El sistema la cierra solo cuando el problema deja de detectarse. Si la resuelve a mano y el problema
              sigue, se abrirá de nuevo.
            </p>
          )}
        </Card>

        <Card title="Historial">
          <ul className="timeline">
            {alert.history.map((item, i) => (
              <li key={i}>
                <strong>{item.action}</strong>
                <span className="muted">
                  {item.userName ?? 'Sistema'} · {dateTime(item.at)}
                </span>
                {item.note && <p className="timeline-note">{item.note}</p>}
              </li>
            ))}
          </ul>
        </Card>
      </div>

      <div className="ficha-side">
        <Card title="Equipo">
          {alert.machineId !== null ? (
            <dl className="info-list">
              <div>
                <dt>Nombre</dt>
                <dd>
                  <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(alert.machineId!)}>
                    {alert.machineName}
                  </button>
                </dd>
              </div>
              <div>
                <dt>Estado actual</dt>
                <dd>{alert.machineStatus ? <StatusBadge status={alert.machineStatus} /> : '—'}</dd>
              </div>
              <div>
                <dt>Sucursal</dt>
                <dd>{alert.branchName ?? '—'}</dd>
              </div>
            </dl>
          ) : (
            <p className="muted">Alerta de red (sin equipo asociado).</p>
          )}
          {alert.deviceName && (
            <p className="muted note">
              Dispositivo de red: <span className="mono">{alert.deviceName}</span>
            </p>
          )}
        </Card>
        <Card title="Seguimiento">
          <dl className="info-list">
            <div>
              <dt>Regla</dt>
              <dd>{alert.ruleName ?? (alert.automatic ? 'Regla por defecto' : '—')}</dd>
            </div>
            <div>
              <dt>Atendida por</dt>
              <dd>{alert.handledBy ?? '—'}</dd>
            </div>
            <div>
              <dt>Resuelta</dt>
              <dd>{alert.resolvedAt ? dateTime(alert.resolvedAt) : '—'}</dd>
            </div>
            <div>
              <dt>Repeticiones (30 días)</dt>
              <dd className={alert.recurrences > 0 ? 'is-late' : undefined}>
                {alert.recurrences === 0 ? 'Primera vez' : `${alert.recurrences} veces antes`}
              </dd>
            </div>
          </dl>
          {tickets &&
            linkedTickets.map((t) => (
              <button key={t.id} type="button" className="ticket-alert" onClick={() => tickets.onOpen(t.id)}>
                <Icon name="ticket" size={14} />
                <span>
                  <strong>Ticket #{t.id}</strong> {t.title} · {t.assignedTo ?? 'sin asignar'}
                </span>
              </button>
            ))}
        </Card>
      </div>
    </div>
  );
}

function AlertActionDialog({
  alert,
  action,
  onClose,
}: {
  alert: AlertDetail;
  action: 'atender' | 'resolver';
  onClose: () => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [note, setNote] = useState('');

  const save = useMutation({
    mutationFn: () =>
      action === 'atender'
        ? api.attendAlert(alert.id, note.trim() || null)
        : api.resolveAlert(alert.id, note.trim() || null),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['alerta', alert.id] });
      void queries.invalidateQueries({ queryKey: ['alertas'] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
      if (alert.machineId !== null) void queries.invalidateQueries({ queryKey: ['maquina', alert.machineId] });
      onClose();
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={action === 'atender' ? 'Atender alerta' : 'Resolver alerta'}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="alert-action" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : action === 'atender' ? 'Tomar la alerta' : 'Marcar como resuelta'}
          </button>
        </>
      }
    >
      <form id="alert-action" className="form-grid form-grid-single" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error" role="alert">
            {save.error.message}
          </p>
        )}
        <p className="muted">
          {action === 'atender'
            ? 'La alerta pasa a "En proceso" a su nombre.'
            : alert.automatic
              ? 'Si el problema sigue, el sistema abrirá una alerta nueva en el próximo minuto.'
              : 'La alerta se cierra.'}
        </p>
        <label className="field">
          <span>Nota (opcional)</span>
          <textarea
            rows={3}
            maxLength={500}
            value={note}
            placeholder={action === 'resolver' ? 'Qué se hizo para resolverla' : 'Qué se va a revisar'}
            onChange={(e) => setNote(e.target.value)}
            autoFocus
          />
        </label>
      </form>
    </Modal>
  );
}

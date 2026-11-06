import { useState, type FormEvent } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { MaintenanceItem, MaintenanceStatus } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { usePanelApi } from '../data/PanelData';
import { useDebounced } from '../data/useDebounced';

const MONTHS = [
  'enero',
  'febrero',
  'marzo',
  'abril',
  'mayo',
  'junio',
  'julio',
  'agosto',
  'septiembre',
  'octubre',
  'noviembre',
  'diciembre',
];
const WEEKDAYS = ['Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb', 'Dom'];
const STATUS_LABEL: Record<MaintenanceStatus, string> = {
  programado: 'Programado',
  vencido: 'Vencido',
  realizado: 'Hecho',
};
const TYPE_LABEL = { preventivo: 'Preventivo', correctivo: 'Correctivo' } as const;

/** AAAA-MM-DD de una fecha local (sin pasar por UTC, que cambiaría el día de noche). */
export function isoDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Días del mes en semanas de lunes a domingo, con los días vecinos para completar las semanas. */
export function monthGrid(year: number, month: number): Date[] {
  const first = new Date(year, month, 1);
  const start = new Date(year, month, 1 - ((first.getDay() + 6) % 7));
  const days = new Date(year, month + 1, 0).getDate();
  const weeks = Math.ceil((((first.getDay() + 6) % 7) + days) / 7);
  return Array.from(
    { length: weeks * 7 },
    (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i),
  );
}

/** El día en que se muestra un mantenimiento: el que se hizo o, si no, el programado. */
const dayOf = (m: MaintenanceItem) => m.completedDate ?? m.scheduledDate ?? '';

export interface ScheduleRequest {
  machineId?: number;
  machineName?: string;
  date?: string;
}

interface CalendarioPageProps {
  canEdit: boolean;
  onOpenEquipo: (id: number) => void;
}

/** W-27 Calendario de mantenimiento: preventivos programados y correctivos hechos, mes a mes. */
export function CalendarioPage({ canEdit, onOpenEquipo }: CalendarioPageProps) {
  const api = usePanelApi();
  const today = new Date();
  const [month, setMonth] = useState({ year: today.getFullYear(), month: today.getMonth() });
  const [sucursal, setSucursal] = useState('');
  const [selected, setSelected] = useState<MaintenanceItem | null>(null);
  const [creating, setCreating] = useState<ScheduleRequest | null>(null);

  const days = monthGrid(month.year, month.month);
  const filter = {
    desde: isoDate(days[0]!),
    hasta: isoDate(days.at(-1)!),
    sucursal: sucursal ? Number(sucursal) : null,
  };
  const list = useQuery({
    queryKey: ['mantenimientos', filter],
    queryFn: () => api.maintenances(filter),
    placeholderData: keepPreviousData,
  });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const move = (delta: number) =>
    setMonth(({ year, month: m }) => ({ year: year + Math.floor((m + delta) / 12), month: (m + delta + 12) % 12 }));
  const todayIso = isoDate(today);

  return (
    <>
      <PageHeader
        title="Calendario de mantenimiento"
        subtitle="Preventivos programados y correctivos realizados"
        actions={
          canEdit && (
            <button type="button" className="btn btn-primary" onClick={() => setCreating({})}>
              <Icon name="plus" size={16} /> Programar mantenimiento
            </button>
          )
        }
      />
      <div className="filters">
        <div className="calendar-nav">
          <button type="button" className="btn btn-outline" aria-label="Mes anterior" onClick={() => move(-1)}>
            ‹
          </button>
          <strong className="calendar-month">
            {MONTHS[month.month]} {month.year}
          </strong>
          <button type="button" className="btn btn-outline" aria-label="Mes siguiente" onClick={() => move(1)}>
            ›
          </button>
          <button
            type="button"
            className="link-quiet"
            onClick={() => setMonth({ year: today.getFullYear(), month: today.getMonth() })}
          >
            Hoy
          </button>
        </div>
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
        {(list.data?.overdue ?? 0) > 0 && (
          <span className="calendar-overdue" role="status">
            {list.data!.overdue} {list.data!.overdue === 1 ? 'mantenimiento vencido' : 'mantenimientos vencidos'}
          </span>
        )}
      </div>

      {list.isPending ? (
        <Loading />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : (
        <div className="calendar card" role="grid" aria-label={`${MONTHS[month.month]} ${month.year}`}>
          <div className="calendar-row calendar-head" role="row">
            {WEEKDAYS.map((d) => (
              <span key={d} role="columnheader">
                {d}
              </span>
            ))}
          </div>
          {Array.from({ length: days.length / 7 }, (_, week) => (
            <div key={week} className="calendar-row" role="row">
              {days.slice(week * 7, week * 7 + 7).map((day) => {
                const iso = isoDate(day);
                const items = list.data.items.filter((m) => dayOf(m) === iso);
                return (
                  <div
                    key={iso}
                    role="gridcell"
                    aria-label={`${day.getDate()} de ${MONTHS[day.getMonth()]}: ${items.length} mantenimientos`}
                    className="calendar-day"
                    data-outside={day.getMonth() !== month.month || undefined}
                    data-today={iso === todayIso || undefined}
                  >
                    <span className="calendar-date mono">{day.getDate()}</span>
                    {items.map((m) => (
                      <button
                        key={m.id}
                        type="button"
                        className="calendar-chip"
                        data-status={m.status}
                        onClick={() => setSelected(m)}
                      >
                        <span className="mono">{m.machineName}</span>
                        <span>{TYPE_LABEL[m.type]}</span>
                      </button>
                    ))}
                    {canEdit && iso >= todayIso && (
                      <button
                        type="button"
                        className="calendar-add"
                        aria-label={`Programar el ${day.getDate()} de ${MONTHS[day.getMonth()]}`}
                        onClick={() => setCreating({ date: iso })}
                      >
                        +
                      </button>
                    )}
                  </div>
                );
              })}
            </div>
          ))}
        </div>
      )}

      {selected && (
        <MaintenanceDetail
          item={selected}
          canEdit={canEdit}
          onClose={() => setSelected(null)}
          onOpenEquipo={(id) => {
            setSelected(null);
            onOpenEquipo(id);
          }}
        />
      )}
      {creating && <MaintenanceForm request={creating} onClose={() => setCreating(null)} />}
    </>
  );
}

function MaintenanceDetail({
  item,
  canEdit,
  onClose,
  onOpenEquipo,
}: {
  item: MaintenanceItem;
  canEdit: boolean;
  onClose: () => void;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [note, setNote] = useState('');
  const [editing, setEditing] = useState(false);
  const done = useMutation({
    mutationFn: () => api.completeMaintenance(item.id, null, note.trim() || null),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['mantenimientos'] });
      void queries.invalidateQueries({ queryKey: ['historial', item.machineId] });
      onClose();
    },
  });
  const remove = useMutation({
    mutationFn: () => api.deleteMaintenance(item.id),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['mantenimientos'] });
      onClose();
    },
  });

  if (editing)
    return (
      <MaintenanceForm
        item={item}
        request={{ machineId: item.machineId, machineName: item.machineName }}
        onClose={onClose}
      />
    );

  const error = done.error ?? remove.error;
  return (
    <Modal
      title={`${TYPE_LABEL[item.type]} · ${item.machineName}`}
      onClose={onClose}
      footer={
        canEdit && item.status !== 'realizado' ? (
          <>
            <button
              type="button"
              className="btn btn-outline"
              disabled={remove.isPending}
              onClick={() => remove.mutate()}
            >
              Eliminar
            </button>
            <button type="button" className="btn btn-outline" onClick={() => setEditing(true)}>
              Editar
            </button>
            <button type="button" className="btn btn-primary" disabled={done.isPending} onClick={() => done.mutate()}>
              Marcar como hecho
            </button>
          </>
        ) : (
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cerrar
          </button>
        )
      }
    >
      {error && (
        <p className="auth-error" role="alert">
          {error.message}
        </p>
      )}
      <dl className="info-list">
        <div>
          <dt>Equipo</dt>
          <dd>
            <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(item.machineId)}>
              {item.machineName}
            </button>
          </dd>
        </div>
        <div>
          <dt>Estado</dt>
          <dd>
            <span className="calendar-chip-status" data-status={item.status}>
              {STATUS_LABEL[item.status]}
            </span>
          </dd>
        </div>
        <div>
          <dt>Programado</dt>
          <dd className="mono">{item.scheduledDate ?? '—'}</dd>
        </div>
        <div>
          <dt>Hecho</dt>
          <dd className="mono">{item.completedDate ?? '—'}</dd>
        </div>
        <div>
          <dt>Técnico</dt>
          <dd>{item.technicianName ?? 'Sin asignar'}</dd>
        </div>
        <div>
          <dt>Sucursal</dt>
          <dd>{item.branchName}</dd>
        </div>
      </dl>
      {item.description && <p>{item.description}</p>}
      {canEdit && item.status !== 'realizado' && (
        <label className="field">
          <span>Nota al marcarlo hecho (queda en el historial del equipo)</span>
          <textarea maxLength={500} rows={2} value={note} onChange={(e) => setNote(e.target.value)} />
        </label>
      )}
    </Modal>
  );
}

/** Programar un preventivo o registrar un correctivo ya hecho (también desde la ficha del equipo). */
export function MaintenanceForm({
  item,
  request,
  onClose,
}: {
  item?: MaintenanceItem;
  request: ScheduleRequest;
  onClose: () => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [search, setSearch] = useState('');
  const q = useDebounced(search.trim());
  const [form, setForm] = useState({
    maquinaId: request.machineId ? String(request.machineId) : '',
    tipo: item?.type ?? 'preventivo',
    descripcion: item?.description ?? '',
    fechaProgramada: item?.scheduledDate ?? request.date ?? '',
    fechaRealizada: item?.completedDate ?? '',
    tecnicoId: item?.technicianId ? String(item.technicianId) : '',
  });
  const machines = useQuery({
    queryKey: ['maquinas', 'buscar', q],
    queryFn: () => api.machines({ q, limit: 30 }),
    enabled: !request.machineId,
  });
  const technicians = useQuery({ queryKey: ['tickets', 'tecnicos'], queryFn: () => api.technicians() });
  const save = useMutation({
    mutationFn: () =>
      api.saveMaintenance(item?.id ?? null, {
        maquinaId: Number(form.maquinaId),
        tipo: form.tipo as 'preventivo' | 'correctivo',
        descripcion: form.descripcion.trim() || null,
        fechaProgramada: form.fechaProgramada || null,
        fechaRealizada: form.fechaRealizada || null,
        tecnicoId: form.tecnicoId ? Number(form.tecnicoId) : null,
      }),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['mantenimientos'] });
      void queries.invalidateQueries({ queryKey: ['historial', Number(form.maquinaId)] });
      onClose();
    },
  });
  const set = (key: keyof typeof form) => (e: { target: { value: string } }) =>
    setForm({ ...form, [key]: e.target.value });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={item ? 'Editar mantenimiento' : 'Programar mantenimiento'}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="maintenance-form" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
        </>
      }
    >
      <form id="maintenance-form" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        {request.machineId ? (
          <p className="form-span">
            Equipo: <strong className="mono">{request.machineName}</strong>
          </p>
        ) : (
          <>
            <label className="field">
              <span>Buscar equipo</span>
              <input
                type="search"
                value={search}
                placeholder="Nombre o IP"
                onChange={(e) => setSearch(e.target.value)}
              />
            </label>
            <label className="field">
              <span>Equipo *</span>
              <select required value={form.maquinaId} onChange={set('maquinaId')}>
                <option value="">Elija…</option>
                {(machines.data?.data.data ?? []).map((m) => (
                  <option key={m.id} value={m.id}>
                    {m.name} · {m.branchName}
                  </option>
                ))}
              </select>
            </label>
          </>
        )}
        <label className="field">
          <span>Tipo *</span>
          <select value={form.tipo} onChange={set('tipo')}>
            <option value="preventivo">Preventivo</option>
            <option value="correctivo">Correctivo</option>
          </select>
        </label>
        <label className="field">
          <span>Técnico</span>
          <select value={form.tecnicoId} onChange={set('tecnicoId')}>
            <option value="">Sin asignar</option>
            {(technicians.data ?? []).map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Fecha programada</span>
          <input type="date" value={form.fechaProgramada} onChange={set('fechaProgramada')} />
        </label>
        <label className="field">
          <span>Fecha en que se hizo</span>
          <input type="date" max={isoDate(new Date())} value={form.fechaRealizada} onChange={set('fechaRealizada')} />
        </label>
        <label className="field form-span">
          <span>Qué se hace</span>
          <textarea
            maxLength={500}
            rows={3}
            value={form.descripcion}
            placeholder="Limpieza interna, cambio de pasta térmica…"
            onChange={set('descripcion')}
          />
        </label>
        <p className="muted form-span">Un correctivo que ya se hizo solo necesita la fecha en que se hizo.</p>
      </form>
    </Modal>
  );
}

import { useEffect, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ErrorGroupItem, ErrorGroupKey, ErrorGroupState, MachineDetail } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Icon } from '@components/Icon';
import { count, dateTime, timeAgo } from '../data/format';
import { usePanelApi } from '../data/PanelData';
import { needsTwoFactor, TwoFactorPrompt } from './remoto/TwoFactorPrompt';

/** Texto propuesto para el ticket que se abre desde un grupo de errores. */
export interface ErrorTicketPrefill {
  title: string;
  description: string;
  priority: 'media' | 'alta';
}

export interface ErrorsTabProps {
  machine: MachineDetail;
  /** alertas.gestionar: marcar resuelto o ignorado. */
  canManage: boolean;
  /** scripts.ejecutar y el equipo con agente: reparar con un comando. */
  canFix: boolean;
  /** Quien ve todas las sucursales puede ignorar en toda la empresa. */
  canIgnoreCompanyWide: boolean;
  onCreateTicket?: (prefill: ErrorTicketPrefill) => void;
}

const RANGES = [
  { days: 1, label: '24H' },
  { days: 7, label: '7D' },
  { days: 30, label: '30D' },
] as const;

type Filter = 'abiertos' | 'resueltos' | 'ignorados' | 'todos';

const STATE_LABEL: Record<ErrorGroupState, string> = {
  abierto: 'Abierto',
  reaparecio: 'Reapareció',
  resuelto: 'Resuelto',
  ignorado: 'Ignorado',
};

const isOpen = (g: ErrorGroupItem) => g.state === 'abierto' || g.state === 'reaparecio';
const keyOf = (g: ErrorGroupItem): ErrorGroupKey => ({ origen: g.source, codigo: g.code });
const idOf = (g: ErrorGroupItem) => `${g.source ?? ''}|${g.code ?? ''}`;
/** Lo que se ve como título: el del catálogo, o la primera línea del mensaje de Windows. */
const titleOf = (g: ErrorGroupItem) => g.title ?? g.lastMessage?.split(/\r?\n/)[0]?.slice(0, 160) ?? 'Error del sistema';

/**
 * Pestaña Errores (decisión 91): los errores de Windows del equipo agrupados por origen y código, con lo que significan,
 * lo que se puede hacer desde Centinela (reiniciar el servicio, marcar resuelto, ignorar, abrir un ticket) y su estado.
 */
export function ErrorsTab({ machine, canManage, canFix, canIgnoreCompanyWide, onCreateTicket }: ErrorsTabProps) {
  const api = usePanelApi();
  const [days, setDays] = useState<number>(7);
  const [filter, setFilter] = useState<Filter>('abiertos');
  const [expanded, setExpanded] = useState<string | null>(null);
  const query = useQuery({
    queryKey: ['errores', machine.id, days],
    queryFn: () => api.errorGroups(machine.id, days),
  });

  if (query.isPending) return <Loading variant="table" rows={6} columns={4} />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;

  const groups = query.data.groups;
  const tally = {
    abiertos: groups.filter(isOpen).length,
    resueltos: groups.filter((g) => g.state === 'resuelto').length,
    ignorados: groups.filter((g) => g.state === 'ignorado').length,
    todos: groups.length,
  };
  const shown = groups.filter((g) =>
    filter === 'todos'
      ? true
      : filter === 'abiertos'
        ? isOpen(g)
        : filter === 'resueltos'
          ? g.state === 'resuelto'
          : g.state === 'ignorado',
  );

  return (
    <div className="errors-tab">
      <div className="errors-toolbar">
        <div className="errors-summary mono">
          <strong>{count(query.data.openTotal, 'error abierto', 'errores abiertos')}</strong>
          <span>· {count(tally.abiertos, 'grupo')} por atender</span>
        </div>
        <div className="errors-filters" role="group" aria-label="Estado">
          {(
            [
              ['abiertos', 'Abiertos'],
              ['resueltos', 'Resueltos'],
              ['ignorados', 'Ignorados'],
              ['todos', 'Todos'],
            ] as const
          ).map(([value, label]) => (
            <button
              key={value}
              type="button"
              className="chip"
              aria-pressed={filter === value}
              onClick={() => setFilter(value)}
            >
              {label} <span className="mono">{tally[value]}</span>
            </button>
          ))}
        </div>
        <div className="segmented" role="group" aria-label="Rango de tiempo">
          {RANGES.map((r) => (
            <button key={r.days} type="button" aria-pressed={days === r.days} onClick={() => setDays(r.days)}>
              {r.label}
            </button>
          ))}
        </div>
      </div>

      {shown.length === 0 ? (
        <EmptyState
          title={
            groups.length === 0
              ? 'Sin errores del sistema en este rango'
              : filter === 'abiertos'
                ? 'No hay errores por atender'
                : 'Nada en esta vista'
          }
        >
          {groups.length > 0 && filter === 'abiertos' && 'Los resueltos e ignorados están en sus filtros.'}
        </EmptyState>
      ) : (
        <ul className="error-groups">
          {shown.map((group) => (
            <ErrorGroupRow
              key={idOf(group)}
              machine={machine}
              group={group}
              open={expanded === idOf(group)}
              onToggle={() => setExpanded((current) => (current === idOf(group) ? null : idOf(group)))}
              canManage={canManage}
              canFix={canFix}
              canIgnoreCompanyWide={canIgnoreCompanyWide}
              onCreateTicket={onCreateTicket}
            />
          ))}
        </ul>
      )}
    </div>
  );
}

interface RowProps extends Omit<ErrorsTabProps, 'machine'> {
  machine: MachineDetail;
  group: ErrorGroupItem;
  open: boolean;
  onToggle: () => void;
}

function ErrorGroupRow({ machine, group, open, onToggle, canManage, canFix, canIgnoreCompanyWide, onCreateTicket }: RowProps) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [form, setForm] = useState<null | 'resuelto' | 'ignorado'>(null);
  const [note, setNote] = useState('');
  const [scope, setScope] = useState<'equipo' | 'empresa'>(group.noise && canIgnoreCompanyWide ? 'empresa' : 'equipo');
  const [showAll, setShowAll] = useState(false);

  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['errores', machine.id] });
    void queries.invalidateQueries({ queryKey: ['maquina', machine.id] });
  };
  const mark = useMutation({
    mutationFn: (estado: 'resuelto' | 'ignorado') =>
      api.markErrorGroup(machine.id, {
        ...keyOf(group),
        estado,
        alcance: estado === 'ignorado' ? scope : 'equipo',
        nota: note.trim() || null,
      }),
    onSuccess: () => {
      setForm(null);
      setNote('');
      refresh();
    },
  });
  const reopen = useMutation({
    mutationFn: () => api.reopenErrorGroup(machine.id, { ...keyOf(group), alcance: group.stateScope ?? 'equipo' }),
    onSuccess: refresh,
  });
  const occurrences = useQuery({
    queryKey: ['errores', machine.id, 'ocurrencias', idOf(group)],
    queryFn: () => api.errorOccurrences(machine.id, keyOf(group)),
    enabled: showAll,
  });

  const origin = [group.source ?? 'Windows', group.code].filter((v) => v !== null).join(' ');
  const shownCount = isOpen(group) ? group.openCount : group.count;
  const ticket = () =>
    onCreateTicket?.({
      title: `${titleOf(group)} (${origin})`,
      description: [
        group.detail,
        group.advice && `Qué hacer: ${group.advice}`,
        `${origin}: ${count(group.count, 'vez', 'veces')} entre ${dateTime(group.firstAt)} y ${dateTime(group.lastAt)}.`,
        group.lastMessage && `Último mensaje de Windows:\n${group.lastMessage}`,
      ]
        .filter(Boolean)
        .join('\n\n'),
      priority: group.level === 'critico' ? 'alta' : 'media',
    });

  return (
    <li className="error-group" data-level={group.level} data-state={group.state} data-open={open || undefined}>
      <button type="button" className="error-group-head" aria-expanded={open} onClick={onToggle}>
        <span className="error-group-count mono" title="Veces en el rango">
          ×{shownCount.toLocaleString('es-DO')}
        </span>
        <span className="error-group-main">
          <strong>{titleOf(group)}</strong>
          <span className="mono">
            {origin} · último {timeAgo(group.lastAt)}
            {group.count > 1 && ` · desde ${dateTime(group.firstAt)}`}
          </span>
        </span>
        {group.service && isOpen(group) && (
          <span className="error-group-fixable mono" title={`Se puede reiniciar ${group.service} desde aquí`}>
            <Icon name="wrench" size={12} />
            REPARABLE
          </span>
        )}
        <span className="error-state mono" data-state={group.state}>
          {STATE_LABEL[group.state]}
        </span>
        <Icon name="expand" size={14} />
      </button>

      {open && (
        <div className="error-group-body">
          {group.detail && <p className="error-group-detail">{group.detail}</p>}
          {group.advice && (
            <div className="what-to-do">
              <Icon name="wrench" size={14} />
              <span>
                <strong>Qué hacer:</strong> {group.advice}
              </span>
            </div>
          )}
          {!group.title && !group.detail && (
            <p className="muted note">
              Error sin explicación en el catálogo de Centinela: revise el mensaje de Windows o abra un ticket.
            </p>
          )}
          {group.lastMessage && <pre className="event-raw mono">{group.lastMessage}</pre>}

          {group.state !== 'abierto' && group.stateAt && (
            <p className="error-group-mark mono">
              {group.state === 'ignorado'
                ? `Ignorado ${group.stateScope === 'empresa' ? 'en toda la empresa' : 'en este equipo'}`
                : 'Resuelto'}{' '}
              por {group.stateBy ?? 'un técnico'} · {dateTime(group.stateAt)}
              {group.state === 'reaparecio' && ` · volvió a ocurrir ${count(group.openCount, 'vez', 'veces')}`}
              {group.stateNote && ` · «${group.stateNote}»`}
            </p>
          )}

          <div className="error-group-actions">
            {canFix && group.service && group.state !== 'ignorado' && (
              <FixButton machineId={machine.id} group={group} onDone={refresh} />
            )}
            {canManage && isOpen(group) && (
              <button type="button" className="btn btn-outline btn-sm" onClick={() => setForm('resuelto')}>
                <Icon name="check" size={14} />
                Marcar resuelto
              </button>
            )}
            {canManage && group.state !== 'ignorado' && (
              <button type="button" className="btn btn-outline btn-sm" onClick={() => setForm('ignorado')}>
                <Icon name="eye" size={14} />
                Ignorar{group.noise ? ' (ruido de Windows)' : ''}
              </button>
            )}
            {canManage && (group.state === 'resuelto' || group.state === 'ignorado') && (
              <button
                type="button"
                className="btn btn-outline btn-sm"
                disabled={reopen.isPending || (group.stateScope === 'empresa' && !canIgnoreCompanyWide)}
                title={
                  group.stateScope === 'empresa' && !canIgnoreCompanyWide
                    ? 'Lo ignoró alguien que ve todas las sucursales'
                    : undefined
                }
                onClick={() => reopen.mutate()}
              >
                <Icon name="refresh" size={14} />
                {group.state === 'ignorado' ? 'Dejar de ignorar' : 'Reabrir'}
              </button>
            )}
            {onCreateTicket && (
              <button type="button" className="btn btn-outline btn-sm" onClick={ticket}>
                <Icon name="ticket" size={14} />
                Crear ticket
              </button>
            )}
            <button type="button" className="link-quiet" onClick={() => setShowAll((v) => !v)}>
              {showAll ? 'Ocultar ocurrencias' : `Ver ocurrencias (${group.count.toLocaleString('es-DO')})`}
            </button>
          </div>
          {reopen.isError && (
            <p className="auth-error" role="alert">
              {reopen.error.message}
            </p>
          )}

          {form && (
            <form
              className="error-group-form"
              onSubmit={(event) => {
                event.preventDefault();
                mark.mutate(form);
              }}
            >
              {form === 'ignorado' && (
                <fieldset className="error-scope">
                  <legend>¿Dónde se ignora?</legend>
                  <label>
                    <input type="radio" checked={scope === 'equipo'} onChange={() => setScope('equipo')} />
                    Solo en este equipo
                  </label>
                  <label title={canIgnoreCompanyWide ? undefined : 'Solo quien ve todas las sucursales'}>
                    <input
                      type="radio"
                      checked={scope === 'empresa'}
                      disabled={!canIgnoreCompanyWide}
                      onChange={() => setScope('empresa')}
                    />
                    En todos los equipos de la empresa
                  </label>
                </fieldset>
              )}
              <label className="field">
                <span>
                  {form === 'resuelto' ? 'Qué se hizo' : 'Por qué se ignora'} <span className="muted">(opcional)</span>
                </span>
                <textarea
                  rows={2}
                  maxLength={500}
                  value={note}
                  onChange={(e) => setNote(e.target.value)}
                  placeholder={form === 'resuelto' ? 'Ej. Se limitó la memoria de SQL Server a 12 GB' : 'Ej. Aviso de Windows sin efecto'}
                />
              </label>
              {form === 'resuelto' && (
                <p className="muted note">Si el error vuelve a ocurrir, reaparece como abierto.</p>
              )}
              {mark.isError && (
                <p className="auth-error" role="alert">
                  {mark.error.message}
                </p>
              )}
              <div className="error-group-actions">
                <button type="submit" className="btn btn-primary btn-sm" disabled={mark.isPending}>
                  {form === 'resuelto' ? 'Marcar resuelto' : 'Ignorar'}
                </button>
                <button type="button" className="btn btn-outline btn-sm" onClick={() => setForm(null)}>
                  Cancelar
                </button>
              </div>
            </form>
          )}

          {showAll &&
            (occurrences.isPending ? (
              <Loading variant="table" rows={3} columns={2} />
            ) : occurrences.isError ? (
              <ErrorState error={occurrences.error} onRetry={() => void occurrences.refetch()} />
            ) : (
              <table className="data-table data-table-compact error-occurrences">
                <thead>
                  <tr>
                    <th>Fecha</th>
                    <th>Mensaje</th>
                  </tr>
                </thead>
                <tbody>
                  {occurrences.data.map((o) => (
                    <tr key={o.id}>
                      <td className="mono">{dateTime(o.at)}</td>
                      <td className="mono">{o.message ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ))}
        </div>
      )}
    </li>
  );
}

/** "Reiniciar servicio": comando firmado al agente (pide el 2FA reciente) y su resultado. */
function FixButton({ machineId, group, onDone }: { machineId: number; group: ErrorGroupItem; onDone: () => void }) {
  const api = usePanelApi();
  const [batch, setBatch] = useState<string | null>(null);
  const [askCode, setAskCode] = useState(false);
  const fix = useMutation({
    mutationFn: () => api.fixErrorGroup(machineId, keyOf(group)),
    onSuccess: ({ lote }) => setBatch(lote),
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });
  const run = useQuery({
    queryKey: ['ejecuciones', batch],
    queryFn: () => api.remoto.runDetail(batch!),
    enabled: batch !== null,
    refetchInterval: (q) => {
      const status = q.state.data?.commands[0]?.status;
      return status === 'completado' || status === 'error' || status === 'expirado' ? false : 2_000;
    },
  });
  const command = run.data?.commands[0];
  const done = command?.status === 'completado' && command.exitCode === 0;
  const failed =
    command &&
    (command.status === 'error' ||
      command.status === 'expirado' ||
      (command.status === 'completado' && command.exitCode !== 0));
  // Una vez por reparación (onDone cambia en cada render).
  const notified = useRef<string | null>(null);
  useEffect(() => {
    if (!done || notified.current === batch) return;
    notified.current = batch;
    onDone();
  }, [done, batch, onDone]);

  const waiting = fix.isPending || (batch !== null && !done && !failed);
  return (
    <span className="start-service">
      <button
        type="button"
        className="btn btn-primary btn-sm"
        disabled={waiting || done}
        onClick={() => {
          setBatch(null);
          fix.mutate();
        }}
      >
        <Icon name="power" size={14} />
        {done
          ? `${group.service} reiniciado`
          : waiting
            ? command?.status === 'pendiente'
              ? 'Esperando al equipo…'
              : 'Reiniciando…'
            : `Reiniciar ${group.service}`}
      </button>
      {done && <span className="muted note">Si el error no vuelve, márquelo resuelto.</span>}
      {fix.isError && !needsTwoFactor(fix.error) && (
        <span className="start-service-error" role="alert">
          {fix.error.message}
        </span>
      )}
      {failed && (
        <span className="start-service-error" role="alert">
          {command.status === 'expirado'
            ? 'El equipo no recibió el comando a tiempo.'
            : `No se pudo reiniciar: ${command.output?.trim() || 'el servicio no arrancó.'}`}
        </span>
      )}
      {askCode && (
        <TwoFactorPrompt
          onConfirmed={() => {
            setAskCode(false);
            fix.mutate();
          }}
          onCancel={() => setAskCode(false)}
        />
      )}
    </span>
  );
}

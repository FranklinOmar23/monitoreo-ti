import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  COMMAND_STATUS_LABELS,
  LANGUAGE_LABELS,
  SESSION_TYPE_LABELS,
  type CommandItem,
  type ScriptItem,
  type ScriptLanguage,
} from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { Tabs } from '@components/Tabs';
import { usePanelApi } from '../../data/PanelData';
import { dateTime, timeAgo } from '../../data/format';
import { RunScriptDialog } from './RunScriptDialog';
import { ScheduledTasks } from './ScheduledTasks';

interface RemotoPageProps {
  canRun: boolean;
  canEdit: boolean;
  /** Ver el historial de sesiones (cualquier permiso remoto). */
  canSessions?: boolean;
  /** Cambiar si el usuario del equipo debe aceptar la conexión (usuarios.administrar). */
  canConfigure?: boolean;
  /** Ejecución a abrir al entrar (recién lanzada desde la ficha o la lista de equipos). */
  initialBatchId?: string | null;
  onOpenEquipo: (id: number) => void;
}

/** Acceso remoto: ejecuciones (W-32) y biblioteca de scripts (W-33). Las sesiones llegan con el relay. */
export function RemotoPage({
  canRun,
  canEdit,
  canSessions = false,
  canConfigure = false,
  initialBatchId,
  onOpenEquipo,
}: RemotoPageProps) {
  const canScripts = canRun || canEdit;
  const [tab, setTab] = useState(canRun ? 'ejecuciones' : canScripts ? 'scripts' : 'sesiones');
  const [batchId, setBatchId] = useState<string | null>(initialBatchId ?? null);
  const [running, setRunning] = useState<{ scriptId?: number } | null>(null);

  const tabs = [
    ...(canRun ? [{ id: 'ejecuciones', label: 'Ejecuciones' }] : []),
    ...(canRun ? [{ id: 'tareas', label: 'Tareas programadas' }] : []),
    ...(canScripts ? [{ id: 'scripts', label: 'Biblioteca de scripts' }] : []),
    ...(canSessions ? [{ id: 'sesiones', label: 'Historial de sesiones' }] : []),
  ];

  return (
    <div className="page">
      <PageHeader
        title="Acceso remoto"
        subtitle="Scripts firmados y sesiones remotas: el agente rechaza lo que no venga firmado por su empresa."
        actions={
          canRun && (
            <button type="button" className="btn btn-primary" onClick={() => setRunning({})}>
              <Icon name="terminal" size={16} /> Ejecutar script
            </button>
          )
        }
      />
      <Tabs items={tabs} activeId={tab} onChange={setTab} label="Secciones de acceso remoto" />

      {tab === 'ejecuciones' &&
        (batchId ? (
          <RunDetailView batchId={batchId} onBack={() => setBatchId(null)} onOpenEquipo={onOpenEquipo} />
        ) : (
          <RunsList onOpen={setBatchId} />
        ))}
      {tab === 'sesiones' && (
        <>
          {canConfigure && <ConsentSetting />}
          <SessionHistory onOpenEquipo={onOpenEquipo} />
        </>
      )}
      {tab === 'tareas' && (
        <ScheduledTasks
          onOpenRun={(id) => {
            setTab('ejecuciones');
            setBatchId(id);
          }}
        />
      )}
      {tab === 'scripts' && (
        <ScriptLibrary canEdit={canEdit} onRun={canRun ? (scriptId) => setRunning({ scriptId }) : undefined} />
      )}

      {running && (
        <RunScriptDialog
          scriptId={running.scriptId}
          onClose={() => setRunning(null)}
          onStarted={(id) => {
            setRunning(null);
            setTab('ejecuciones');
            setBatchId(id);
          }}
        />
      )}
    </div>
  );
}

// ------------------------------------------------------------------ ejecuciones

function RunsList({ onOpen }: { onOpen: (batchId: string) => void }) {
  const api = usePanelApi();
  const runs = useQuery({ queryKey: ['ejecuciones'], queryFn: () => api.remoto.runs() });

  if (runs.isPending) return <Loading />;
  if (runs.isError) return <ErrorState error={runs.error} onRetry={() => void runs.refetch()} />;
  if (runs.data.length === 0)
    return <EmptyState title="Todavía no se ha ejecutado nada">Use "Ejecutar script" para lanzar uno.</EmptyState>;

  return (
    <div className="table-card">
      <table className="data-table">
        <thead>
          <tr>
            <th>Qué</th>
            <th>Quién</th>
            <th>Cuándo</th>
            <th>Equipos</th>
          </tr>
        </thead>
        <tbody>
          {runs.data.map((r) => (
            <tr key={r.batchId}>
              <td>
                <button type="button" className="link-quiet" onClick={() => onOpen(r.batchId)}>
                  {r.title}
                </button>
                {r.language && <span className="muted"> · {LANGUAGE_LABELS[r.language]}</span>}
              </td>
              <td>{r.userName}</td>
              <td title={dateTime(r.createdAt)}>{timeAgo(r.createdAt)}</td>
              <td>
                <RunProgress completed={r.completed} failed={r.failed} pending={r.pending + r.running} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RunProgress({ completed, failed, pending }: { completed: number; failed: number; pending: number }) {
  return (
    <span className="run-progress">
      {completed > 0 && <span data-tone="ok">{completed} bien</span>}
      {failed > 0 && <span data-tone="bad">{failed} con error</span>}
      {pending > 0 && <span data-tone="wait">{pending} en curso</span>}
    </span>
  );
}

function RunDetailView({
  batchId,
  onBack,
  onOpenEquipo,
}: {
  batchId: string;
  onBack: () => void;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const detail = useQuery({ queryKey: ['ejecuciones', batchId], queryFn: () => api.remoto.runDetail(batchId) });
  const [open, setOpen] = useState<number | null>(null);

  if (detail.isPending) return <Loading />;
  if (detail.isError) return <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />;
  const { summary, content, commands } = detail.data;

  return (
    <section className="run-detail">
      <button type="button" className="link-quiet" onClick={onBack}>
        ← Todas las ejecuciones
      </button>
      <h2>{summary.title}</h2>
      <p className="muted">
        {summary.userName} · {dateTime(summary.createdAt)} ·{' '}
        <RunProgress
          completed={summary.completed}
          failed={summary.failed}
          pending={summary.pending + summary.running}
        />
      </p>
      <pre className="code-block" aria-label="Contenido ejecutado">
        {content}
      </pre>
      <div className="table-card">
        <table className="data-table">
          <thead>
            <tr>
              <th>Equipo</th>
              <th>Estado</th>
              <th>Código</th>
              <th>Terminó</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {commands.map((c) => (
              <CommandRow
                key={c.id}
                command={c}
                open={open === c.id}
                onToggle={() => setOpen(open === c.id ? null : c.id)}
                onOpenEquipo={onOpenEquipo}
              />
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function CommandRow({
  command: c,
  open,
  onToggle,
  onOpenEquipo,
}: {
  command: CommandItem;
  open: boolean;
  onToggle: () => void;
  onOpenEquipo: (id: number) => void;
}) {
  return (
    <>
      <tr>
        <td>
          <button type="button" className="link-quiet" onClick={() => onOpenEquipo(c.machineId)}>
            {c.machineName}
          </button>
          <span className="muted"> · {c.branchName}</span>
        </td>
        <td>
          <span className="command-status" data-status={c.status}>
            {COMMAND_STATUS_LABELS[c.status]}
          </span>
        </td>
        <td className="mono">{c.exitCode ?? '—'}</td>
        <td>{c.completedAt ? dateTime(c.completedAt) : '—'}</td>
        <td>
          {c.output !== null && (
            <button type="button" className="link-quiet" aria-expanded={open} onClick={onToggle}>
              {open ? 'Ocultar salida' : 'Ver salida'}
            </button>
          )}
        </td>
      </tr>
      {open && (
        <tr className="command-output">
          <td colSpan={5}>
            <pre className="code-block" aria-label={`Salida de ${c.machineName}`}>
              {c.output || '(sin salida)'}
            </pre>
          </td>
        </tr>
      )}
    </>
  );
}

// ------------------------------------------------------------------ permiso del usuario (decisión 52)

function ConsentSetting() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const settings = useQuery({ queryKey: ['remoto', 'configuracion'], queryFn: () => api.remoto.remoteSettings() });
  const save = useMutation({
    mutationFn: (pedirPermiso: boolean) => api.remoto.saveRemoteSettings({ pedirPermiso }),
    onSuccess: (data) => queries.setQueryData(['remoto', 'configuracion'], data),
  });
  if (!settings.data) return null;
  return (
    <label className="checkbox-field consent-setting">
      <input
        type="checkbox"
        checked={settings.data.pedirPermiso}
        disabled={save.isPending}
        onChange={(e) => save.mutate(e.target.checked)}
      />
      <span>
        Pedir permiso al usuario del equipo antes de ver su pantalla (si no responde en 30 segundos, no se conecta). El
        aviso de sesión activa se muestra siempre.
      </span>
    </label>
  );
}

// ------------------------------------------------------------------ historial de sesiones (W-34)

function duration(seconds: number | null): string {
  if (seconds === null) return '—';
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.round(seconds / 60);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

function SessionHistory({ onOpenEquipo }: { onOpenEquipo: (id: number) => void }) {
  const api = usePanelApi();
  const [page, setPage] = useState(1);
  const sessions = useQuery({ queryKey: ['sesiones', page], queryFn: () => api.remoto.sessions({ pagina: page }) });

  if (sessions.isPending) return <Loading />;
  if (sessions.isError) return <ErrorState error={sessions.error} onRetry={() => void sessions.refetch()} />;
  const { items, total, pageSize } = sessions.data;
  if (items.length === 0)
    return (
      <EmptyState title="No hay sesiones todavía">Aquí queda cada terminal, archivo o escritorio abierto.</EmptyState>
    );

  return (
    <div className="table-card">
      <table className="data-table">
        <thead>
          <tr>
            <th>Equipo</th>
            <th>Tipo</th>
            <th>Técnico</th>
            <th>Inicio</th>
            <th>Duración</th>
            <th>Desde</th>
          </tr>
        </thead>
        <tbody>
          {items.map((s) => (
            <tr key={s.id}>
              <td>
                <button type="button" className="link-quiet" onClick={() => onOpenEquipo(s.machineId)}>
                  {s.machineName}
                </button>
                <span className="muted"> · {s.branchName}</span>
              </td>
              <td>
                {SESSION_TYPE_LABELS[s.type]}
                {s.viewOnly && <span className="muted"> · solo ver</span>}
                {s.reason && <div className="muted session-reason">{s.reason}</div>}
                {s.active && (
                  <span className="command-status" data-status="ejecutando">
                    {' '}
                    activa
                  </span>
                )}
              </td>
              <td>{s.userName}</td>
              <td>{dateTime(s.startedAt)}</td>
              <td>{s.active ? 'en curso' : duration(s.durationSeconds)}</td>
              <td className="mono">{s.sourceIp ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {total > pageSize && (
        <div className="pager">
          <button type="button" className="btn btn-outline" disabled={page === 1} onClick={() => setPage(page - 1)}>
            Anterior
          </button>
          <span className="muted">
            Página {page} de {Math.ceil(total / pageSize)}
          </span>
          <button
            type="button"
            className="btn btn-outline"
            disabled={page * pageSize >= total}
            onClick={() => setPage(page + 1)}
          >
            Siguiente
          </button>
        </div>
      )}
    </div>
  );
}

// ------------------------------------------------------------------ biblioteca

function ScriptLibrary({ canEdit, onRun }: { canEdit: boolean; onRun?: (scriptId: number) => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const scripts = useQuery({ queryKey: ['scripts'], queryFn: () => api.remoto.scripts() });
  const [editing, setEditing] = useState<ScriptItem | 'nuevo' | null>(null);
  const remove = useMutation({
    mutationFn: (id: number) => api.remoto.deleteScript(id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['scripts'] }),
  });

  if (scripts.isPending) return <Loading />;
  if (scripts.isError) return <ErrorState error={scripts.error} onRetry={() => void scripts.refetch()} />;

  return (
    <section>
      {canEdit && (
        <div className="section-actions">
          <button type="button" className="btn btn-outline" onClick={() => setEditing('nuevo')}>
            <Icon name="plus" size={16} /> Nuevo script
          </button>
        </div>
      )}
      {scripts.data.length === 0 ? (
        <EmptyState title="La biblioteca está vacía">
          Guarde aquí los scripts que usa a menudo: quedan firmados y listos para ejecutar.
        </EmptyState>
      ) : (
        <ul className="script-list">
          {scripts.data.map((s) => (
            <li key={s.id} className="script-card">
              <div>
                <strong>{s.name}</strong>
                <span className="muted">
                  {' '}
                  · {LANGUAGE_LABELS[s.language]}
                  {s.signed ? ' · firmado' : ' · sin firma'}
                </span>
                {s.description && <p className="muted">{s.description}</p>}
                <p className="muted">
                  {s.createdBy}
                  {s.lastRunAt ? ` · última vez ${timeAgo(s.lastRunAt)}` : ' · nunca ejecutado'}
                </p>
              </div>
              <div className="script-actions">
                {onRun && (
                  <button type="button" className="btn btn-outline" onClick={() => onRun(s.id)}>
                    Ejecutar
                  </button>
                )}
                {canEdit && (
                  <>
                    <button type="button" className="btn btn-outline" onClick={() => setEditing(s)}>
                      Editar
                    </button>
                    <button
                      type="button"
                      className="link-quiet"
                      onClick={() => {
                        if (window.confirm(`¿Eliminar el script "${s.name}"? Las ejecuciones pasadas se conservan.`))
                          remove.mutate(s.id);
                      }}
                    >
                      Eliminar
                    </button>
                  </>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      {editing && <ScriptForm script={editing === 'nuevo' ? null : editing} onClose={() => setEditing(null)} />}
    </section>
  );
}

function ScriptForm({ script, onClose }: { script: ScriptItem | null; onClose: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [form, setForm] = useState({
    nombre: script?.name ?? '',
    lenguaje: script?.language ?? ('powershell' as ScriptLanguage),
    descripcion: script?.description ?? '',
    contenido: script?.content ?? '',
  });
  const save = useMutation({
    mutationFn: () =>
      api.remoto.saveScript(script?.id ?? null, {
        nombre: form.nombre.trim(),
        lenguaje: form.lenguaje,
        descripcion: form.descripcion.trim() || null,
        contenido: form.contenido,
      }),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['scripts'] });
      onClose();
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={script ? `Editar · ${script.name}` : 'Nuevo script'}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="script-form" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : 'Guardar y firmar'}
          </button>
        </>
      }
    >
      <form id="script-form" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field">
          <span>Nombre *</span>
          <input
            required
            maxLength={150}
            value={form.nombre}
            onChange={(e) => setForm({ ...form, nombre: e.target.value })}
            autoFocus
          />
        </label>
        <label className="field">
          <span>Lenguaje</span>
          <select
            value={form.lenguaje}
            onChange={(e) => setForm({ ...form, lenguaje: e.target.value as ScriptLanguage })}
          >
            {(['powershell', 'cmd', 'python'] as const).map((l) => (
              <option key={l} value={l}>
                {LANGUAGE_LABELS[l]}
              </option>
            ))}
          </select>
        </label>
        <label className="field form-span">
          <span>Descripción</span>
          <input
            maxLength={500}
            value={form.descripcion}
            placeholder="Qué hace y cuándo usarlo"
            onChange={(e) => setForm({ ...form, descripcion: e.target.value })}
          />
        </label>
        <label className="field form-span">
          <span>Contenido *</span>
          <textarea
            required
            className="code-input"
            rows={12}
            maxLength={100_000}
            spellCheck={false}
            value={form.contenido}
            onChange={(e) => setForm({ ...form, contenido: e.target.value })}
          />
        </label>
      </form>
    </Modal>
  );
}

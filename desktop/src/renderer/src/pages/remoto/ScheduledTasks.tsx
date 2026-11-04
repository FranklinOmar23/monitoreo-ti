import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  WEEK_DAYS,
  type ScheduledTaskInput,
  type ScheduledTaskItem,
  type TaskFrequency,
  type TaskTarget,
} from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../../data/PanelData';
import { dateTime } from '../../data/format';
import { MachinePicker, type MachineRef } from './RunScriptDialog';
import { needsTwoFactor, TwoFactorPrompt } from './TwoFactorPrompt';

const FREQUENCIES: { value: TaskFrequency; label: string }[] = [
  { value: 'diaria', label: 'Todos los días' },
  { value: 'semanal', label: 'Cada semana' },
  { value: 'mensual', label: 'Cada mes' },
  { value: 'una_vez', label: 'Una sola vez' },
];

const TARGETS: { value: TaskTarget; label: string }[] = [
  { value: 'equipos', label: 'Equipos elegidos' },
  { value: 'sucursal', label: 'Una sucursal' },
  { value: 'todos', label: 'Todos los equipos' },
];

/** Tareas programadas (6.5): scripts de la biblioteca que se ejecutan solos, en hora de República Dominicana. */
export function ScheduledTasks({ onOpenRun }: { onOpenRun: (batchId: string) => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const tasks = useQuery({ queryKey: ['tareas'], queryFn: () => api.remoto.tasks() });
  const [editing, setEditing] = useState<ScheduledTaskItem | 'nueva' | null>(null);
  const remove = useMutation({
    mutationFn: (id: number) => api.remoto.deleteTask(id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['tareas'] }),
  });

  if (tasks.isPending) return <Loading />;
  if (tasks.isError) return <ErrorState error={tasks.error} onRetry={() => void tasks.refetch()} />;

  return (
    <section>
      <div className="section-actions">
        <span className="muted">Horas en hora de República Dominicana.</span>
        <button type="button" className="btn btn-outline" onClick={() => setEditing('nueva')}>
          <Icon name="plus" size={16} /> Nueva tarea
        </button>
      </div>
      {tasks.data.length === 0 ? (
        <EmptyState title="No hay tareas programadas">
          Programe un script de la biblioteca para que se ejecute solo: por ejemplo, limpiar temporales cada noche.
        </EmptyState>
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Tarea</th>
                <th>Cuándo</th>
                <th>Dónde</th>
                <th>Próxima</th>
                <th>Última</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {tasks.data.map((t) => (
                <tr key={t.id}>
                  <td>
                    <strong>{t.name}</strong>
                    <span className="muted"> · {t.scriptName}</span>
                    {!t.active && <span className="command-status"> en pausa</span>}
                  </td>
                  <td>{t.description}</td>
                  <td>
                    {t.target === 'todos'
                      ? 'Todos los equipos'
                      : t.target === 'sucursal'
                        ? (t.branchName ?? 'Sucursal')
                        : `${t.machineIds.length} ${t.machineIds.length === 1 ? 'equipo' : 'equipos'}`}
                  </td>
                  <td>{t.active && t.nextRunAt ? dateTime(t.nextRunAt) : '—'}</td>
                  <td>
                    {t.lastRunAt && t.lastBatchId ? (
                      <button type="button" className="link-quiet" onClick={() => onOpenRun(t.lastBatchId!)}>
                        {dateTime(t.lastRunAt)}
                      </button>
                    ) : t.lastRunAt ? (
                      dateTime(t.lastRunAt)
                    ) : (
                      'Nunca'
                    )}
                  </td>
                  <td className="file-row-actions">
                    <button type="button" className="link-quiet" onClick={() => setEditing(t)}>
                      Editar
                    </button>
                    <button
                      type="button"
                      className="link-quiet"
                      onClick={() => window.confirm(`¿Eliminar la tarea "${t.name}"?`) && remove.mutate(t.id)}
                    >
                      Eliminar
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && <TaskForm task={editing === 'nueva' ? null : editing} onClose={() => setEditing(null)} />}
    </section>
  );
}

function TaskForm({ task, onClose }: { task: ScheduledTaskItem | null; onClose: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const scripts = useQuery({ queryKey: ['scripts'], queryFn: () => api.remoto.scripts() });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const [form, setForm] = useState({
    nombre: task?.name ?? '',
    scriptId: task ? String(task.scriptId) : '',
    objetivo: task?.target ?? ('equipos' as TaskTarget),
    sucursalId: task?.branchId ? String(task.branchId) : '',
    frecuencia: task?.frequency ?? ('diaria' as TaskFrequency),
    hora: task?.time ?? '02:00',
    fecha: task?.date ?? '',
    diasSemana: task?.weekDays ?? 31,
    diaMes: String(task?.monthDay ?? 1),
    timeout: String(task?.timeoutSeconds ?? 300),
    activa: task?.active ?? true,
  });
  const [machines, setMachines] = useState<MachineRef[]>(
    (task?.machineIds ?? []).map((id) => ({ id, name: `Equipo ${id}` })),
  );
  const [askCode, setAskCode] = useState(false);

  const input = (): ScheduledTaskInput => ({
    nombre: form.nombre.trim(),
    scriptId: Number(form.scriptId),
    objetivo: form.objetivo,
    sucursalId: form.objetivo === 'sucursal' ? Number(form.sucursalId) : null,
    maquinaIds: form.objetivo === 'equipos' ? machines.map((m) => m.id) : null,
    frecuencia: form.frecuencia,
    hora: form.hora,
    fecha: form.frecuencia === 'una_vez' ? form.fecha : null,
    diasSemana: form.frecuencia === 'semanal' ? form.diasSemana : null,
    diaMes: form.frecuencia === 'mensual' ? Number(form.diaMes) : null,
    timeoutS: Number(form.timeout),
    activa: form.activa,
  });
  const save = useMutation({
    mutationFn: () => api.remoto.saveTask(task?.id ?? null, input()),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['tareas'] });
      onClose();
    },
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <>
      <Modal
        title={task ? `Editar · ${task.name}` : 'Nueva tarea programada'}
        onClose={onClose}
        wide
        footer={
          <>
            <button type="button" className="btn btn-outline" onClick={onClose}>
              Cancelar
            </button>
            <button type="submit" form="task-form" className="btn btn-primary" disabled={save.isPending}>
              {save.isPending ? 'Guardando…' : 'Guardar'}
            </button>
          </>
        }
      >
        <form id="task-form" className="form-grid" onSubmit={submit}>
          {save.isError && !needsTwoFactor(save.error) && (
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
            <span>Script *</span>
            <select required value={form.scriptId} onChange={(e) => setForm({ ...form, scriptId: e.target.value })}>
              <option value="">Elija un script</option>
              {(scripts.data ?? []).map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Cuándo</span>
            <select
              value={form.frecuencia}
              onChange={(e) => setForm({ ...form, frecuencia: e.target.value as TaskFrequency })}
            >
              {FREQUENCIES.map((f) => (
                <option key={f.value} value={f.value}>
                  {f.label}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>Hora</span>
            <input
              type="time"
              required
              value={form.hora}
              onChange={(e) => setForm({ ...form, hora: e.target.value })}
            />
          </label>
          {form.frecuencia === 'una_vez' && (
            <label className="field">
              <span>Fecha</span>
              <input
                type="date"
                required
                value={form.fecha}
                onChange={(e) => setForm({ ...form, fecha: e.target.value })}
              />
            </label>
          )}
          {form.frecuencia === 'semanal' && (
            <fieldset className="field form-span week-days">
              <legend>Días</legend>
              {WEEK_DAYS.map((d) => (
                <label key={d.bit}>
                  <input
                    type="checkbox"
                    checked={(form.diasSemana & d.bit) !== 0}
                    onChange={() => setForm({ ...form, diasSemana: form.diasSemana ^ d.bit })}
                  />{' '}
                  {d.label}
                </label>
              ))}
            </fieldset>
          )}
          {form.frecuencia === 'mensual' && (
            <label className="field">
              <span>Día del mes</span>
              <input
                type="number"
                min={1}
                max={31}
                value={form.diaMes}
                onChange={(e) => setForm({ ...form, diaMes: e.target.value })}
              />
            </label>
          )}
          <label className="field">
            <span>Dónde</span>
            <select
              value={form.objetivo}
              onChange={(e) => setForm({ ...form, objetivo: e.target.value as TaskTarget })}
            >
              {TARGETS.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
          </label>
          {form.objetivo === 'sucursal' && (
            <label className="field">
              <span>Sucursal</span>
              <select
                required
                value={form.sucursalId}
                onChange={(e) => setForm({ ...form, sucursalId: e.target.value })}
              >
                <option value="">Elija la sucursal</option>
                {(branches.data ?? []).map((b) => (
                  <option key={b.id} value={b.id}>
                    {b.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          {form.objetivo === 'equipos' && (
            <div className="form-span">
              <MachinePicker chosen={machines} onChange={setMachines} />
            </div>
          )}
          <label className="field">
            <span>Tiempo límite (segundos)</span>
            <input
              type="number"
              min={10}
              max={3600}
              value={form.timeout}
              onChange={(e) => setForm({ ...form, timeout: e.target.value })}
            />
          </label>
          <label className="field checkbox-field">
            <input
              type="checkbox"
              checked={form.activa}
              onChange={(e) => setForm({ ...form, activa: e.target.checked })}
            />
            <span>Activa (si no, queda en pausa)</span>
          </label>
          <p className="form-span muted">
            Se ejecuta a nombre suyo, con permisos del sistema. Un equipo apagado la recibe al conectarse, hasta 24
            horas.
          </p>
        </form>
      </Modal>
      {askCode && (
        <TwoFactorPrompt
          onCancel={() => setAskCode(false)}
          onConfirmed={() => {
            setAskCode(false);
            save.mutate();
          }}
        />
      )}
    </>
  );
}

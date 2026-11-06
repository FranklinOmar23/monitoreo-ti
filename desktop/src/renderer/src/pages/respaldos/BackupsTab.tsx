import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { BackupJobItem, BackupKind, BackupRunItem, BackupRunStatus, MachineDetail } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { dateTime, timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { needsTwoFactor, TwoFactorPrompt } from '../remoto/TwoFactorPrompt';
import { BackupJobDialog } from './BackupJobDialog';

export const RUN_STATUS_LABEL: Record<BackupRunStatus, string> = {
  en_curso: 'En curso',
  correcto: 'Correcto',
  advertencia: 'Con avisos',
  error: 'Error',
};

export const KIND_LABEL: Record<BackupKind, string> = {
  completo: 'Completo',
  incremental: 'Incremental',
  diferencial: 'Diferencial',
};

const DAYS = ['dom', 'lun', 'mar', 'mié', 'jue', 'vie', 'sáb'];

export function bytes(value: number): string {
  if (value >= 1024 ** 4) return `${(value / 1024 ** 4).toFixed(1)} TB`;
  if (value >= 1024 ** 3) return `${(value / 1024 ** 3).toFixed(1)} GB`;
  if (value >= 1024 ** 2) return `${Math.round(value / 1024 ** 2)} MB`;
  return `${Math.max(0, Math.round(value / 1024))} KB`;
}

function duration(from: string, to: string | null): string {
  if (!to) return '—';
  const seconds = Math.max(0, Math.round((new Date(to).getTime() - new Date(from).getTime()) / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.round(seconds / 60);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** "SQL Server · ERP_Caribe → Disco WD Elements · diario 23:00 · solo lo nuevo (completa cada 7 días)". */
export function jobSummary(job: BackupJobItem): { what: string; where: string; when: string; how: string } {
  const what =
    job.source === 'sql'
      ? `SQL Server ${job.sqlInstance === 'MSSQLSERVER' ? '' : `(${job.sqlInstance}) `}· ${job.items.join(', ')}`
      : `Carpetas · ${job.items.join(', ')}`;
  const where =
    job.destinationType === 'disco_externo'
      ? `Disco ${job.disks.map((d) => d.nombre).join(' / ')}`
      : job.destinationType === 'red'
        ? job.destinationPath
        : `Local ${job.destinationPath}`;
  const when =
    job.frequency === 'diario'
      ? `Diario ${job.time}`
      : `${job.weekDays.map((d) => DAYS[d]).join(', ')} ${job.time}`;
  const how = [
    job.strategy === 'completo' ? 'Completo cada vez' : `Solo lo nuevo (completa cada ${job.fullEveryDays} días)`,
    job.mode === 'frio' ? `en frío (${job.stopServices.length} servicios)` : 'en caliente',
  ].join(' · ');
  return { what, where, when, how };
}

/**
 * Pestaña Respaldos de la ficha de un servidor (decisión 82): resumen, avisos, los trabajos con "Respaldar ahora" y el
 * historial. Mientras un respaldo avanza, se consulta cada 5 s.
 */
export function BackupsTab({ machine, canManage }: { machine: MachineDetail; canManage: boolean }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [editing, setEditing] = useState<BackupJobItem | 'nuevo' | null>(null);
  // Lo que se reintenta después de confirmar el 2FA.
  const [retry, setRetry] = useState<(() => void) | null>(null);
  const [askCode, setAskCode] = useState(false);
  const overview = useQuery({
    queryKey: ['respaldos', machine.id],
    queryFn: () => api.respaldos.overview(machine.id),
    refetchInterval: (q) => (q.state.data?.runs.some((r) => r.status === 'en_curso') ? 5_000 : 60_000),
  });
  const run = useMutation({
    mutationFn: ({ job, full }: { job: BackupJobItem; full: boolean }) => api.respaldos.runNow(machine.id, job.id, full),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['respaldos', machine.id] }),
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });
  const remove = useMutation({
    mutationFn: (job: BackupJobItem) => api.respaldos.remove(machine.id, job.id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['respaldos', machine.id] }),
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });

  if (overview.isPending) return <Loading variant="table" rows={5} columns={5} />;
  if (overview.isError) return <ErrorState error={overview.error} onRetry={() => void overview.refetch()} />;
  const data = overview.data;

  if (!data.isServer)
    return (
      <EmptyState title="Los respaldos son para servidores">
        Este equipo no está marcado como servidor. Si lo es, cámbielo en Editar → Tipo: Servidor (el agente ya marca solo
        las ediciones Windows Server).
      </EmptyState>
    );

  const running = new Map(data.runs.filter((r) => r.status === 'en_curso').map((r) => [r.jobId, r]));
  const lastOk = data.jobs.map((j) => j.lastSuccessAt).filter((d): d is string => d !== null).sort().at(-1) ?? null;
  const next = data.jobs.filter((j) => j.isActive && j.nextRunAt).map((j) => j.nextRunAt!).sort()[0] ?? null;
  const problems = data.jobs.filter(
    (j) => j.isActive && (j.lastStatus === 'error' || j.servicesNotRestarted || j.diskMissingSince),
  );
  const mutationError = run.error ?? remove.error;

  return (
    <div className="backups">
      {!data.agentSupportsBackups && (
        <p className="notice" role="status">
          <Icon name="bolt" size={16} />
          {data.agentVersion?.includes('clasico') ? (
            <span>
              Este servidor tiene el <strong>agente clásico</strong> (para Windows 7 y 8), que no hace respaldos. Si es
              Windows Server 2016 o más nuevo, desinstale el clásico e instale el agente normal 0.4.0 o superior.
            </span>
          ) : (
            <span>
              El agente de este servidor es la versión {data.agentVersion ?? '?'}: los respaldos necesitan la 0.4.0 o
              superior. Actualícelo desde Instaladores (o con la actualización automática).
            </span>
          )}
        </p>
      )}
      {data.agentSupportsBackups && !data.agentConnected && (
        <p className="notice" role="status">
          <Icon name="unplug" size={16} />
          <span>
            El agente no está conectado ahora. Los respaldos programados se hacen igual (el agente tiene su configuración);
            «Respaldar ahora» y los cambios llegan cuando se conecte.
          </span>
        </p>
      )}

      <div className="backup-kpis">
        <div className="card kpi" data-tone={lastOk ? 'sano' : 'desconectado'}>
          <span className="eyebrow">
            <span className="kpi-dot" aria-hidden="true" />
            Último respaldo correcto
          </span>
          <span className="kpi-value">
            <strong className="backup-kpi-text">{lastOk ? timeAgo(lastOk) : 'Nunca'}</strong>
          </span>
          {lastOk && <span className="kpi-note">{dateTime(lastOk)}</span>}
        </div>
        <div className="card kpi" data-tone="neutral">
          <span className="eyebrow">
            <span className="kpi-dot" aria-hidden="true" />
            Próximo
          </span>
          <span className="kpi-value">
            <strong className="backup-kpi-text">{next ? dateTime(next) : '—'}</strong>
          </span>
        </div>
        <div className="card kpi" data-tone={data.failedLast30Days > 0 ? 'advertencia' : 'sano'}>
          <span className="eyebrow">
            <span className="kpi-dot" aria-hidden="true" />
            Últimos 30 días
          </span>
          <span className="kpi-value">
            <strong>{data.okLast30Days}</strong>
            <span className="kpi-unit mono">correctos</span>
          </span>
          <span className="kpi-note">{data.failedLast30Days} con error</span>
        </div>
      </div>

      {problems.map((job) => (
        <div key={job.id} className="backup-strip" data-level={job.diskMissingSince && job.lastStatus !== 'error' && !job.servicesNotRestarted ? 'advertencia' : 'critico'} role="alert">
          <strong>{job.name}</strong>
          <span>
            {job.servicesNotRestarted
              ? `No volvieron a arrancar: ${job.servicesNotRestarted}.`
              : job.lastStatus === 'error'
                ? (job.lastError ?? 'El último respaldo falló.')
                : 'No hay ningún disco del respaldo conectado: conéctelo antes de la hora.'}
          </span>
        </div>
      ))}

      {mutationError && !needsTwoFactor(mutationError) && (
        <p className="api-test-result is-error" role="alert">
          {mutationError.message}
        </p>
      )}

      <Card
        title="Trabajos de respaldo"
        aside={
          canManage && (
            <button
              type="button"
              className="btn btn-primary"
              disabled={!data.agentSupportsBackups}
              title={data.agentSupportsBackups ? undefined : 'Primero instale el agente 0.4.0 o superior en este servidor'}
              onClick={() => setEditing('nuevo')}
            >
              <Icon name="plus" size={16} />
              Nuevo respaldo
            </button>
          )
        }
      >
        {data.jobs.length === 0 ? (
          <p className="muted">
            Sin respaldos todavía.{' '}
            {canManage
              ? 'Cree uno: por ejemplo, la base de datos a un disco externo cada noche, copiando solo lo nuevo.'
              : 'Pídale a quien administre los respaldos que cree uno.'}
          </p>
        ) : (
          <ul className="backup-jobs">
            {data.jobs.map((job) => (
              <JobRow
                key={job.id}
                job={job}
                running={running.get(job.id) ?? null}
                canManage={canManage}
                busy={run.isPending || remove.isPending || !data.agentSupportsBackups}
                onRun={(full) => {
                  setRetry(() => () => run.mutate({ job, full }));
                  run.mutate({ job, full });
                }}
                onEdit={() => setEditing(job)}
                onDelete={() => {
                  if (window.confirm(`¿Eliminar el respaldo «${job.name}»? Los archivos ya copiados en el destino no se borran.`)) {
                    setRetry(() => () => remove.mutate(job));
                    remove.mutate(job);
                  }
                }}
              />
            ))}
          </ul>
        )}
      </Card>

      <Card title="Historial" aside={<span className="eyebrow">Últimos 30 días</span>}>
        {data.runs.length === 0 ? (
          <p className="muted">Todavía no se ha hecho ningún respaldo.</p>
        ) : (
          <RunsTable runs={data.runs} />
        )}
      </Card>

      {editing && (
        <BackupJobDialog
          machineId={machine.id}
          job={editing === 'nuevo' ? null : editing}
          onClose={() => setEditing(null)}
        />
      )}
      {askCode && (
        <TwoFactorPrompt
          onConfirmed={() => {
            setAskCode(false);
            retry?.();
          }}
          onCancel={() => setAskCode(false)}
        />
      )}
    </div>
  );
}

function JobRow({
  job,
  running,
  canManage,
  busy,
  onRun,
  onEdit,
  onDelete,
}: {
  job: BackupJobItem;
  running: BackupRunItem | null;
  canManage: boolean;
  busy: boolean;
  onRun: (full: boolean) => void;
  onEdit: () => void;
  onDelete: () => void;
}) {
  const summary = jobSummary(job);
  return (
    <li className="backup-job" data-active={job.isActive}>
      <div className="backup-job-main">
        <div className="backup-job-title">
          <Icon name={job.source === 'sql' ? 'database' : 'folder'} size={16} />
          <strong>{job.name}</strong>
          {!job.isActive && <span className="install-status">Pausado</span>}
          {running ? (
            <span className="install-status" data-status="instalando">
              Respaldando… {running.bytesCopied > 0 ? bytes(running.bytesCopied) : ''}
            </span>
          ) : (
            job.lastStatus && (
              <span className="backup-status" data-status={job.lastStatus}>
                {RUN_STATUS_LABEL[job.lastStatus]}
              </span>
            )
          )}
        </div>
        <p className="backup-job-line">
          {summary.what} <span aria-hidden="true">→</span> {summary.where}
        </p>
        <p className="backup-job-line muted">
          {summary.when} · {summary.how}
          {job.isActive && job.nextRunAt ? ` · próximo ${dateTime(job.nextRunAt)}` : ''}
        </p>
        {running?.detail && <p className="backup-job-line muted">{running.detail}</p>}
      </div>
      {canManage && (
        <div className="backup-job-actions">
          <button type="button" className="btn btn-outline" disabled={busy || running !== null} onClick={() => onRun(false)}>
            Respaldar ahora
          </button>
          {job.strategy === 'incremental' && (
            <button
              type="button"
              className="btn btn-outline"
              disabled={busy || running !== null}
              title="La próxima cadena empieza desde esta copia completa"
              onClick={() => onRun(true)}
            >
              Hacer completo ahora
            </button>
          )}
          <button type="button" className="btn btn-outline" onClick={onEdit}>
            Editar
          </button>
          <button type="button" className="btn btn-outline backup-delete" aria-label={`Eliminar ${job.name}`} onClick={onDelete}>
            Eliminar
          </button>
        </div>
      )}
    </li>
  );
}

function RunsTable({ runs }: { runs: BackupRunItem[] }) {
  return (
    <table className="data-table backup-runs">
      <thead>
        <tr>
          <th>Inicio</th>
          <th>Trabajo</th>
          <th>Tipo</th>
          <th>Estado</th>
          <th>Copiado</th>
          <th>Duración</th>
          <th>Destino</th>
          <th>Detalle</th>
        </tr>
      </thead>
      <tbody>
        {runs.map((r) => (
          <tr key={r.id}>
            <td className="mono">{dateTime(r.startedAt)}</td>
            <td>
              {r.jobName}
              {r.trigger === 'manual' && <span className="muted"> · manual{r.requestedBy ? ` (${r.requestedBy})` : ''}</span>}
            </td>
            <td>{r.kind ? KIND_LABEL[r.kind] : '—'}</td>
            <td>
              <span className="backup-status" data-status={r.status}>
                {RUN_STATUS_LABEL[r.status]}
              </span>
            </td>
            <td className="mono">
              {r.bytesCopied > 0 || r.bytesTotal > 0
                ? r.kind && r.kind !== 'completo' && r.bytesTotal > 0
                  ? `${bytes(r.bytesCopied)} de ${bytes(r.bytesTotal)}`
                  : bytes(r.bytesCopied)
                : '—'}
            </td>
            <td className="mono">{duration(r.startedAt, r.finishedAt)}</td>
            <td>
              {r.destination ?? '—'}
              {r.setName && <span className="muted mono backup-set"> {r.setName}</span>}
            </td>
            <td className="backup-detail">{r.detail ?? ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

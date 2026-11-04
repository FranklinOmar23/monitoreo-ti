import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ErrorState, Loading } from '@components/AsyncState';
import { usePanelApi } from '../data/PanelData';

/**
 * Actualización del agente (6.6): versión publicada en el servidor, cuántos equipos están al día y la opción de que se
 * actualicen solos (por tandas de 10). "Actualizar ahora" la ofrece ya a los conectados que están atrasados.
 */
export function AgentUpdates() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const status = useQuery({
    queryKey: ['actualizaciones'],
    queryFn: () => api.remoto.updates(),
    refetchInterval: 30_000,
  });
  const refresh = () => void queries.invalidateQueries({ queryKey: ['actualizaciones'] });
  const toggle = useMutation({ mutationFn: (on: boolean) => api.remoto.setAutoUpdate(on), onSuccess: refresh });
  const deploy = useMutation({ mutationFn: () => api.remoto.deployUpdate(), onSuccess: refresh });

  if (status.isPending) return <Loading />;
  if (status.isError) return <ErrorState error={status.error} onRetry={() => void status.refetch()} />;
  const s = status.data;

  return (
    <section className="agent-updates">
      <div className="update-summary">
        <div>
          <span className="muted">Versión publicada</span>
          <strong className="mono">{s.available ?? '—'}</strong>
        </div>
        <div>
          <span className="muted">Al día</span>
          <strong>
            {s.upToDate} de {s.total}
          </strong>
        </div>
        <div>
          <span className="muted">Atrasados</span>
          <strong>{s.outdated}</strong>
        </div>
        <div>
          <span className="muted">Actualizándose</span>
          <strong>{s.updating}</strong>
        </div>
      </div>

      {s.problem && (
        <p className="auth-error" role="alert">
          {s.problem}
        </p>
      )}

      <div className="update-actions">
        <label className="checkbox-field">
          <input
            type="checkbox"
            checked={s.autoUpdate}
            disabled={toggle.isPending}
            onChange={(e) => toggle.mutate(e.target.checked)}
          />
          <span>Actualizar los agentes automáticamente (de 10 en 10, verificando la firma del producto)</span>
        </label>
        <button
          type="button"
          className="btn btn-primary"
          disabled={!s.available || s.outdated === 0 || deploy.isPending}
          onClick={() => deploy.mutate()}
        >
          Actualizar ahora
        </button>
      </div>
      {deploy.isSuccess && (
        <p className="muted" role="status">
          Se ofreció la versión a {deploy.data.ofrecidos} {deploy.data.ofrecidos === 1 ? 'equipo' : 'equipos'}{' '}
          conectados. Los demás la reciben al conectarse.
        </p>
      )}

      {s.outdatedAgents.length > 0 && (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Equipo</th>
                <th>Sucursal</th>
                <th>Versión</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {s.outdatedAgents.map((a) => (
                <tr key={a.machineId}>
                  <td>{a.machineName}</td>
                  <td>{a.branchName}</td>
                  <td className="mono">{a.version || '—'}</td>
                  <td>{a.updating ? 'Actualizándose…' : a.online ? 'Conectado, en espera' : 'Desconectado'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

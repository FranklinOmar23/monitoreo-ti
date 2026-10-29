import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { ApiError, type CreatedInstallerToken, type InstallerToken, type PendingAgent } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { Tabs } from '@components/Tabs';
import { useSession } from '../auth/SessionProvider';
import { dateTime, timeAgo } from '../data/format';
import { usePanelApi } from '../data/PanelData';

const STATE_LABEL: Record<InstallerToken['state'], { label: string; status?: string }> = {
  activo: { label: 'Activo', status: 'sano' },
  vencido: { label: 'Vencido', status: 'desconectado' },
  agotado: { label: 'Agotado', status: 'advertencia' },
  revocado: { label: 'Revocado', status: 'critico' },
};

const INSTALLER_NAME = 'MonitoreoTI-Agente-Setup.exe';

/** W-18 Instaladores y W-19 Equipos pendientes de aprobación. */
export function InstaladoresPage() {
  const api = usePanelApi();
  const [tab, setTab] = useState('instaladores');
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<CreatedInstallerToken | null>(null);
  const pending = useQuery({ queryKey: ['pendientes'], queryFn: () => api.pendingAgents(), refetchInterval: 30_000 });

  return (
    <>
      <PageHeader
        title="Instaladores"
        subtitle="Instale el agente en los equipos de cada sucursal"
        actions={
          <>
            <DownloadButton />
            <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}>
              Nuevo token de instalación
            </button>
          </>
        }
      />
      <Tabs
        label="Instaladores"
        activeId={tab}
        onChange={setTab}
        items={[
          { id: 'instaladores', label: 'Tokens de instalación' },
          { id: 'pendientes', label: 'Pendientes de aprobación', count: pending.data?.length },
        ]}
      />
      {tab === 'instaladores' ? <TokenList /> : <PendingList query={pending} />}

      {creating && (
        <TokenForm
          onClose={() => setCreating(false)}
          onCreated={(token) => {
            setCreating(false);
            setCreated(token);
          }}
        />
      )}
      {created && <CreatedToken token={created} onClose={() => setCreated(null)} />}
    </>
  );
}

function TokenList() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const tokens = useQuery({ queryKey: ['instaladores'], queryFn: () => api.installers() });
  const revoke = useMutation({
    mutationFn: (id: number) => api.revokeInstaller(id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['instaladores'] }),
  });
  const [deleting, setDeleting] = useState<InstallerToken | null>(null);

  if (tokens.isPending) return <Loading />;
  if (tokens.isError) return <ErrorState error={tokens.error} onRetry={() => void tokens.refetch()} />;
  if (tokens.data.length === 0)
    return (
      <EmptyState title="Todavía no hay tokens de instalación">
        Cree uno para la sucursal donde va a instalar el agente.
      </EmptyState>
    );

  return (
    <div className="table-card">
      <table className="data-table">
        <thead>
          <tr>
            <th>Estado</th>
            <th>Nombre</th>
            <th>Sucursal</th>
            <th>Equipos</th>
            <th>Vence</th>
            <th>Aprobación</th>
            <th>Creado por</th>
            <th aria-label="Acciones" />
          </tr>
        </thead>
        <tbody>
          {tokens.data.map((token) => (
            <tr key={token.id}>
              <td>
                <span className="status-badge" data-status={STATE_LABEL[token.state].status}>
                  <span className="status-dot" aria-hidden="true" />
                  {STATE_LABEL[token.state].label}
                </span>
              </td>
              <td>{token.name ?? '—'}</td>
              <td>{token.branchName}</td>
              <td className="mono">
                {token.uses}
                {token.maxUses !== null && ` / ${token.maxUses}`}
              </td>
              <td className="mono">{dateTime(token.expiresAt)}</td>
              <td>{token.requiresApproval ? 'Requiere aprobación' : 'Automática'}</td>
              <td>
                {token.createdBy ?? '—'}
                <span className="row-sub"> {timeAgo(token.createdAt)}</span>
              </td>
              <td className="row-actions">
                {token.state === 'activo' && (
                  <button
                    type="button"
                    className="btn btn-outline"
                    disabled={revoke.isPending}
                    onClick={() => revoke.mutate(token.id)}
                  >
                    Revocar
                  </button>
                )}
                <button type="button" className="btn btn-outline" onClick={() => setDeleting(token)}>
                  Eliminar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {deleting && <DeleteTokenDialog token={deleting} onClose={() => setDeleting(null)} />}
    </div>
  );
}

/** Confirmación para eliminar un token. Los equipos que ya registró siguen funcionando. */
function DeleteTokenDialog({ token, onClose }: { token: InstallerToken; onClose: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const remove = useMutation({
    mutationFn: () => api.deleteInstaller(token.id),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['instaladores'] });
      onClose();
    },
  });

  return (
    <Modal
      title="Eliminar token de instalación"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="button" className="btn btn-ticket" disabled={remove.isPending} onClick={() => remove.mutate()}>
            {remove.isPending ? 'Eliminando…' : 'Eliminar'}
          </button>
        </>
      }
    >
      {remove.isError && (
        <p className="auth-error" role="alert">
          {remove.error.message}
        </p>
      )}
      <p>
        Se elimina el token {token.name ? `"${token.name}" ` : ''}de {token.branchName}. Ya no podrá registrar equipos.
        {token.uses > 0 && ` Los ${token.uses} equipo(s) que registró siguen funcionando con normalidad.`}
      </p>
    </Modal>
  );
}

function PendingList({ query }: { query: UseQueryResult<PendingAgent[]> }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const decide = useMutation({
    mutationFn: ({ agentId, approve }: { agentId: number; approve: boolean }) =>
      approve ? api.approveAgent(agentId) : api.rejectAgent(agentId),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['pendientes'] });
      void queries.invalidateQueries({ queryKey: ['maquinas'] });
    },
  });

  if (query.isPending) return <Loading />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  if (query.data.length === 0)
    return (
      <EmptyState title="No hay equipos esperando aprobación">
        Aparecen aquí los que se registran con un token que exige aprobación.
      </EmptyState>
    );

  return (
    <div className="table-card">
      <table className="data-table">
        <thead>
          <tr>
            <th>Equipo</th>
            <th>Sucursal</th>
            <th>IP</th>
            <th>MAC</th>
            <th>Sistema</th>
            <th>Registrado</th>
            <th aria-label="Acciones" />
          </tr>
        </thead>
        <tbody>
          {query.data.map((agent) => (
            <tr key={agent.agentId}>
              <td className="mono">{agent.machineName}</td>
              <td>{agent.branchName}</td>
              <td className="mono">{agent.ipAddress ?? '—'}</td>
              <td className="mono">{agent.macAddress ?? '—'}</td>
              <td>{agent.operatingSystem ?? '—'}</td>
              <td className="mono">{timeAgo(agent.registeredAt)}</td>
              <td className="row-actions">
                <button
                  type="button"
                  className="btn btn-primary"
                  disabled={decide.isPending}
                  onClick={() => decide.mutate({ agentId: agent.agentId, approve: true })}
                >
                  Aprobar
                </button>
                <button
                  type="button"
                  className="btn btn-outline"
                  disabled={decide.isPending}
                  onClick={() => decide.mutate({ agentId: agent.agentId, approve: false })}
                >
                  Rechazar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function TokenForm({ onClose, onCreated }: { onClose: () => void; onCreated: (token: CreatedInstallerToken) => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const [form, setForm] = useState({
    sucursalId: '',
    nombre: '',
    diasVigencia: '7',
    usosMax: '',
    requiereAprobacion: false,
  });

  const create = useMutation({
    mutationFn: () =>
      api.createInstaller({
        sucursalId: Number(form.sucursalId),
        nombre: form.nombre.trim() || null,
        diasVigencia: Number(form.diasVigencia),
        usosMax: form.usosMax ? Number(form.usosMax) : null,
        requiereAprobacion: form.requiereAprobacion,
      }),
    onSuccess: (token) => {
      void queries.invalidateQueries({ queryKey: ['instaladores'] });
      onCreated(token);
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    create.mutate();
  };

  return (
    <Modal
      title="Nuevo token de instalación"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="token-form" className="btn btn-primary" disabled={create.isPending}>
            {create.isPending ? 'Creando…' : 'Crear token'}
          </button>
        </>
      }
    >
      <form id="token-form" className="form-grid form-grid-single" onSubmit={submit}>
        {create.isError && (
          <p className="auth-error" role="alert">
            {create.error.message}
          </p>
        )}
        <label className="field">
          <span>Sucursal *</span>
          <select required value={form.sucursalId} onChange={(e) => setForm({ ...form, sucursalId: e.target.value })}>
            <option value="">Elija la sucursal</option>
            {(branches.data ?? []).map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Nombre</span>
          <input
            maxLength={100}
            value={form.nombre}
            placeholder="Ej. Cajas del segundo piso"
            onChange={(e) => setForm({ ...form, nombre: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Vigencia (días) *</span>
          <input
            type="number"
            min={1}
            max={365}
            required
            value={form.diasVigencia}
            onChange={(e) => setForm({ ...form, diasVigencia: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Máximo de equipos</span>
          <input
            type="number"
            min={1}
            value={form.usosMax}
            placeholder="Sin límite"
            onChange={(e) => setForm({ ...form, usosMax: e.target.value })}
          />
        </label>
        <label className="check">
          <input
            type="checkbox"
            checked={form.requiereAprobacion}
            onChange={(e) => setForm({ ...form, requiereAprobacion: e.target.checked })}
          />
          Los equipos quedan pendientes hasta que alguien los apruebe
        </label>
      </form>
    </Modal>
  );
}

function CreatedToken({ token, onClose }: { token: CreatedInstallerToken; onClose: () => void }) {
  const [copied, setCopied] = useState<string | null>(null);
  // El portapapeles pasa por el proceso principal: el panel niega los permisos web, incluido el de portapapeles.
  const copy = (label: string, text: string) => {
    const write = window.monitoreo ? window.monitoreo.copyText(text) : navigator.clipboard.writeText(text);
    void write.then(
      () => setCopied(label),
      () => setCopied(null),
    );
  };

  return (
    <Modal
      title="Token creado"
      onClose={onClose}
      wide
      footer={
        <button type="button" className="btn btn-primary" onClick={onClose}>
          Listo
        </button>
      }
    >
      <p className="auth-info">
        Copie el token ahora: por seguridad no se vuelve a mostrar. Vence el {dateTime(token.vence)}.
      </p>
      <div className="secret-box">
        <span className="eyebrow">Token</span>
        <code className="mono">{token.token}</code>
        <button type="button" className="btn btn-outline" onClick={() => copy('token', token.token)}>
          {copied === 'token' ? 'Copiado' : 'Copiar'}
        </button>
      </div>
      <div className="secret-box">
        <span className="eyebrow">Instalación silenciosa (como administrador)</span>
        <code className="mono">{token.comando}</code>
        <button type="button" className="btn btn-outline" onClick={() => copy('comando', token.comando)}>
          {copied === 'comando' ? 'Copiado' : 'Copiar comando'}
        </button>
      </div>
      <div className="secret-box">
        <span className="eyebrow">Dirección del servidor</span>
        <code className="mono">{token.urlServidor}</code>
        <button type="button" className="btn btn-outline" onClick={() => copy('servidor', token.urlServidor)}>
          {copied === 'servidor' ? 'Copiado' : 'Copiar dirección'}
        </button>
      </div>
      <p className="muted">
        Instalación normal: ejecute el instalador y pegue la dirección del servidor y el token en sus campos. Para
        instalar sin preguntas, use el comando de instalación silenciosa.
      </p>
    </Modal>
  );
}

/** Descarga el instalador del agente desde el servidor y lo guarda con el diálogo de Windows. */
function DownloadButton() {
  const { serverUrl, accessToken } = useSession();
  const [message, setMessage] = useState<string | null>(null);
  const download = useMutation({
    mutationFn: async () => {
      const response = await fetch(`${serverUrl.replace(/\/+$/, '')}/api/v1/instaladores/descarga`, {
        headers: { Authorization: `Bearer ${accessToken() ?? ''}` },
      });
      if (!response.ok) {
        const body = (await response.json().catch(() => ({}))) as { title?: string };
        throw new ApiError(response.status, undefined, body.title ?? 'No se pudo descargar el instalador.');
      }
      const data = new Uint8Array(await response.arrayBuffer());
      if (!window.monitoreo) throw new Error('La descarga solo funciona dentro del panel.');
      return window.monitoreo.saveFile(INSTALLER_NAME, data);
    },
    onSuccess: (result) => setMessage(result.error ?? (result.saved ? `Guardado en ${result.path}` : null)),
    onError: (error) => setMessage(error.message),
  });

  return (
    <>
      <button type="button" className="btn btn-outline" disabled={download.isPending} onClick={() => download.mutate()}>
        <Icon name="download" size={16} /> {download.isPending ? 'Descargando…' : 'Descargar instalador'}
      </button>
      {message && (
        <Modal title="Descarga del instalador" onClose={() => setMessage(null)}>
          <p>{message}</p>
        </Modal>
      )}
    </>
  );
}

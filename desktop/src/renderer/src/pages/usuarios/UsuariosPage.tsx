import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { PasswordLink, RoleItem, UserItem } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { Tabs } from '@components/Tabs';
import { initials } from '@components/Topbar';
import { usePanelApi } from '../../data/PanelData';
import { dateTime, timeAgo } from '../../data/format';
import { RolesView } from './RolesView';

/** Rol del sistema que ve todas las sucursales aunque tenga algunas marcadas (decisión 28). */
export const ADMIN_ROLE = 'Administrador';

const STATES = [
  { value: 'activos', label: 'Activos' },
  { value: 'pendientes', label: 'Sin contraseña' },
  { value: 'inactivos', label: 'Inactivos' },
];

/** W-36 Usuarios, W-37 formulario y W-38 Roles y permisos. */
export function UsuariosPage() {
  const [tab, setTab] = useState('usuarios');
  const [editing, setEditing] = useState<UserItem | 'new' | null>(null);
  const [newRole, setNewRole] = useState(false);
  const api = usePanelApi();
  const users = useQuery({ queryKey: ['usuarios'], queryFn: () => api.usuarios.list() });
  const active = users.data?.filter((u) => u.isActive).length ?? 0;

  return (
    <div className="page">
      <PageHeader
        title="Usuarios"
        subtitle={
          users.data
            ? `${users.data.length} usuarios · ${active} activos. Cada uno entra con su correo, su contraseña y, si su rol lo exige, el 2FA.`
            : 'Cargando…'
        }
        actions={
          tab === 'usuarios' ? (
            <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>
              <Icon name="plus" size={16} /> Agregar usuario
            </button>
          ) : (
            <button type="button" className="btn btn-primary" onClick={() => setNewRole(true)}>
              <Icon name="plus" size={16} /> Nuevo rol
            </button>
          )
        }
      />
      <Tabs
        items={[
          { id: 'usuarios', label: 'Usuarios' },
          { id: 'roles', label: 'Roles y permisos' },
        ]}
        activeId={tab}
        onChange={setTab}
        label="Secciones de usuarios"
      />

      {tab === 'usuarios' &&
        (users.isPending ? (
          <Loading />
        ) : users.isError ? (
          <ErrorState error={users.error} onRetry={() => void users.refetch()} />
        ) : (
          <UserList users={users.data} onOpen={setEditing} />
        ))}
      {tab === 'roles' && <RolesView creating={newRole} onCreatingDone={() => setNewRole(false)} />}

      {editing && <UserForm user={editing === 'new' ? undefined : editing} onClose={() => setEditing(null)} />}
    </div>
  );
}

function UserList({ users, onOpen }: { users: UserItem[]; onOpen: (user: UserItem) => void }) {
  const [query, setQuery] = useState('');
  const [role, setRole] = useState('');
  const [state, setState] = useState('');
  const roles = [...new Set(users.map((u) => u.roleName))].sort((a, b) => a.localeCompare(b, 'es'));

  const text = query.trim().toLocaleLowerCase('es');
  const visible = users.filter(
    (u) =>
      (!text || u.name.toLocaleLowerCase('es').includes(text) || u.email.toLocaleLowerCase('es').includes(text)) &&
      (!role || u.roleName === role) &&
      (!state ||
        (state === 'activos' && u.isActive) ||
        (state === 'inactivos' && !u.isActive) ||
        (state === 'pendientes' && u.isActive && u.pending)),
  );

  return (
    <>
      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="Buscar por nombre o correo"
            aria-label="Buscar por nombre o correo"
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
        <FilterSelect
          label="Rol"
          value={role}
          options={roles.map((r) => ({ value: r, label: r }))}
          allLabel="Todos"
          onChange={setRole}
        />
        <FilterSelect label="Estado" value={state} options={STATES} allLabel="Todos" onChange={setState} />
      </div>

      {visible.length === 0 ? (
        <EmptyState title="No hay usuarios con esos filtros" />
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Usuario</th>
                <th>Rol</th>
                <th>Sucursales</th>
                <th>2FA</th>
                <th>Último acceso</th>
                <th>Estado</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((u) => (
                <tr key={u.id} data-inactive={!u.isActive || undefined}>
                  <td>
                    <button type="button" className="user-cell" onClick={() => onOpen(u)}>
                      <span className="avatar avatar-sm" aria-hidden="true">
                        {initials(u.name)}
                      </span>
                      <span>
                        <strong>
                          {u.name}
                          {u.isSelf && <span className="muted"> (usted)</span>}
                        </strong>
                        <span className="muted">{u.email}</span>
                      </span>
                    </button>
                  </td>
                  <td>{u.roleName}</td>
                  <td>{branchesLabel(u)}</td>
                  <td>
                    <TwoFactorBadge user={u} />
                  </td>
                  <td title={dateTime(u.lastLoginAt)}>{u.lastLoginAt ? timeAgo(u.lastLoginAt) : 'Nunca'}</td>
                  <td>
                    <UserState user={u} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function branchesLabel(user: UserItem): string {
  if (user.roleName === ADMIN_ROLE || user.branches.length === 0) return 'Todas';
  return user.branches.map((b) => b.name).join(', ');
}

function TwoFactorBadge({ user }: { user: UserItem }) {
  if (user.twoFactorEnabled)
    return (
      <span className="status-badge" data-status="sano">
        Activo
      </span>
    );
  if (user.twoFactorRequired)
    return (
      <span className="status-badge" data-status="advertencia" title="Lo activará la próxima vez que inicie sesión">
        Pendiente
      </span>
    );
  return <span className="muted">No lo usa</span>;
}

function UserState({ user }: { user: UserItem }) {
  const [status, label] = !user.isActive
    ? ['baja', 'Inactivo']
    : user.lockedOut
      ? ['critico', 'Bloqueado']
      : user.pending
        ? ['advertencia', 'Sin contraseña']
        : ['sano', 'Activo'];
  return (
    <span className="status-badge" data-status={status}>
      <span className="status-dot" aria-hidden="true" />
      {label}
    </span>
  );
}

// ------------------------------------------------------------------ formulario (W-37)

function UserForm({ user, onClose }: { user?: UserItem; onClose: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api.usuarios.roles() });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const [form, setForm] = useState({
    nombre: user?.name ?? '',
    correo: user?.email ?? '',
    rolId: user ? String(user.roleId) : '',
    sucursales: new Set(user?.branches.map((b) => b.id) ?? []),
  });
  const [link, setLink] = useState<{ value: PasswordLink | null; name: string; invited: boolean } | null>(null);

  const role = roles.data?.find((r) => String(r.id) === form.rolId);
  const seesAll = role?.name === ADMIN_ROLE;
  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['usuarios'] });
    void queries.invalidateQueries({ queryKey: ['roles'] });
    void queries.invalidateQueries({ queryKey: ['usuarios-empresa'] });
  };

  const save = useMutation({
    mutationFn: async () => {
      const input = {
        nombre: form.nombre.trim(),
        correo: form.correo.trim(),
        rolId: Number(form.rolId),
        sucursalIds: seesAll ? [] : [...form.sucursales],
      };
      if (user) {
        await api.usuarios.update(user.id, input);
        return null;
      }
      return api.usuarios.create(input);
    },
    onSuccess: (created) => {
      refresh();
      if (created) setLink({ value: created.enlace, name: form.nombre.trim(), invited: true });
      else onClose();
    },
  });

  const toggleBranch = (id: number) => {
    const next = new Set(form.sucursales);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setForm({ ...form, sucursales: next });
  };

  if (link) return <PasswordLinkDialog link={link.value} name={link.name} invited={link.invited} onClose={onClose} />;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={user ? `Editar ${user.name}` : 'Agregar usuario'}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="user-form" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : user ? 'Guardar' : 'Crear y enviar enlace'}
          </button>
        </>
      }
    >
      <form id="user-form" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        {user?.shared && (
          <p className="notice form-span">
            <Icon name="users" size={16} /> {user.name} también pertenece a otra empresa: su nombre, su correo, su
            contraseña y su 2FA solo los cambia él. Aquí se cambian su rol y sus sucursales.
          </p>
        )}
        <label className="field">
          <span>Nombre *</span>
          <input
            required
            maxLength={150}
            value={form.nombre}
            disabled={user?.shared}
            onChange={(e) => setForm({ ...form, nombre: e.target.value })}
            autoFocus
          />
        </label>
        <label className="field">
          <span>Correo *</span>
          <input
            required
            type="email"
            maxLength={200}
            value={form.correo}
            disabled={user?.shared}
            onChange={(e) => setForm({ ...form, correo: e.target.value })}
          />
        </label>
        <label className="field form-span">
          <span>Rol *</span>
          <select
            required
            value={form.rolId}
            disabled={user?.isSelf}
            onChange={(e) => setForm({ ...form, rolId: e.target.value })}
          >
            <option value="">Elija el rol</option>
            {(roles.data ?? []).map((r) => (
              <option key={r.id} value={r.id}>
                {r.name}
                {r.isSystem ? '' : ' (propio)'}
              </option>
            ))}
          </select>
          {user?.isSelf ? (
            <small className="muted">No puede cambiar su propio rol.</small>
          ) : (
            role && <RoleHint role={role} />
          )}
        </label>

        <fieldset className="field form-span branch-picker">
          <legend>Sucursales que ve</legend>
          {seesAll ? (
            <p className="muted">El Administrador ve todas las sucursales.</p>
          ) : (
            <>
              <p className="muted">Sin marcar ninguna, ve todas.</p>
              <div className="branch-checks">
                {(branches.data ?? []).map((b) => (
                  <label key={b.id} className="check">
                    <input type="checkbox" checked={form.sucursales.has(b.id)} onChange={() => toggleBranch(b.id)} />
                    {b.name}
                  </label>
                ))}
              </div>
            </>
          )}
        </fieldset>

        {!user && (
          <p className="muted form-span">
            No escriba una contraseña por el usuario: se le envía un enlace de un solo uso (vence en 72 horas) para que
            cree la suya.
          </p>
        )}
      </form>

      {user && (
        <UserAccess
          user={user}
          onLink={(value) => setLink({ value, name: user.name, invited: false })}
          onDone={onClose}
        />
      )}
    </Modal>
  );
}

function RoleHint({ role }: { role: RoleItem }) {
  const remote = role.permissions.some((p) => p.startsWith('remoto.') || p === 'scripts.ejecutar');
  return (
    <small className="muted">
      {role.description ?? `${role.permissions.length} permisos`}
      {remote && ' · Da acceso remoto: el usuario debe activar la verificación en dos pasos.'}
    </small>
  );
}

/** Contraseña, 2FA y activar/desactivar: acciones que no esperan al botón Guardar. */
function UserAccess({
  user,
  onLink,
  onDone,
}: {
  user: UserItem;
  onLink: (link: PasswordLink) => void;
  onDone: () => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const done = () => void queries.invalidateQueries({ queryKey: ['usuarios'] });

  const link = useMutation({
    mutationFn: () => api.usuarios.passwordLink(user.id),
    onSuccess: (value) => {
      done();
      onLink(value);
    },
  });
  const finish = () => {
    done();
    onDone();
  };
  const twoFactor = useMutation({ mutationFn: () => api.usuarios.resetTwoFactor(user.id), onSuccess: finish });
  const active = useMutation({ mutationFn: () => api.usuarios.setActive(user.id, !user.isActive), onSuccess: finish });
  const error = link.error ?? twoFactor.error ?? active.error;

  return (
    <section className="user-access" aria-label="Acceso">
      <h3>Acceso</h3>
      {error && (
        <p className="auth-error" role="alert">
          {error.message}
        </p>
      )}
      <div className="user-access-row">
        <div>
          <strong>Contraseña</strong>
          <span className="muted">
            {user.pending
              ? 'Todavía no creó su contraseña.'
              : user.lockedOut
                ? 'Bloqueado por intentos fallidos: el enlace también lo desbloquea.'
                : 'Un enlace de un solo uso para crear una nueva. Al usarlo se cierran sus sesiones.'}
          </span>
        </div>
        <button
          type="button"
          className="btn btn-outline"
          disabled={user.shared || !user.isActive || link.isPending}
          onClick={() => link.mutate()}
        >
          {user.pending ? 'Reenviar enlace' : 'Restablecer contraseña'}
        </button>
      </div>
      <div className="user-access-row">
        <div>
          <strong>Verificación en dos pasos</strong>
          <span className="muted">
            {user.twoFactorEnabled
              ? 'Si perdió o cambió el teléfono, reinícielo: al entrar vuelve a escanear el QR.'
              : user.twoFactorRequired
                ? 'La activará la próxima vez que inicie sesión.'
                : 'Su rol no la exige.'}
          </span>
        </div>
        <button
          type="button"
          className="btn btn-outline"
          disabled={user.shared || !user.twoFactorEnabled || twoFactor.isPending}
          onClick={() => {
            if (window.confirm(`¿Reiniciar la verificación en dos pasos de ${user.name}? Se cerrarán sus sesiones.`))
              twoFactor.mutate();
          }}
        >
          Reiniciar 2FA
        </button>
      </div>
      <div className="user-access-row">
        <div>
          <strong>{user.isActive ? 'Usuario activo' : 'Usuario inactivo'}</strong>
          <span className="muted">
            {user.isActive
              ? 'Al desactivarlo no puede entrar a esta empresa y se cierran sus sesiones. No se borra su historial.'
              : 'No puede entrar a esta empresa. Actívelo para devolverle el acceso.'}
          </span>
        </div>
        <button
          type="button"
          className={user.isActive ? 'btn btn-danger' : 'btn btn-outline'}
          disabled={user.isSelf || active.isPending}
          onClick={() => {
            if (!user.isActive || window.confirm(`¿Desactivar a ${user.name}?`)) active.mutate();
          }}
        >
          {user.isActive ? 'Desactivar' : 'Activar'}
        </button>
      </div>
    </section>
  );
}

/** Resultado de crear o restablecer: el enlace se entrega por correo o, sin correo configurado, a mano. */
function PasswordLinkDialog({
  link,
  name,
  invited,
  onClose,
}: {
  link: PasswordLink | null;
  name: string;
  invited: boolean;
  onClose: () => void;
}) {
  const [copied, setCopied] = useState(false);
  const copy = (text: string) => {
    const write = window.monitoreo ? window.monitoreo.copyText(text) : navigator.clipboard.writeText(text);
    void Promise.resolve(write).then(() => setCopied(true));
  };

  return (
    <Modal
      title={invited ? 'Usuario creado' : 'Enlace de contraseña'}
      onClose={onClose}
      footer={
        <button type="button" className="btn btn-primary" onClick={onClose}>
          Listo
        </button>
      }
    >
      {!link ? (
        <p>
          {name} ya tenía cuenta en otra empresa: entra con su correo y su contraseña de siempre, y ahora puede elegir
          esta empresa.
        </p>
      ) : link.emailed ? (
        <p>
          Se envió a {name} un correo con el enlace para {invited ? 'crear' : 'cambiar'} su contraseña. Vence el{' '}
          {dateTime(link.expiresAt)}.
        </p>
      ) : (
        <>
          <p>
            El servidor no tiene correo configurado: entregue este enlace a {name} por un medio seguro. Lo abre o lo
            pega en "¿Olvidó su contraseña?" del panel. Sirve una sola vez y vence el {dateTime(link.expiresAt)}.
          </p>
          <div className="secret-box">
            <span className="eyebrow">Enlace</span>
            <code className="mono">{link.link}</code>
            <button type="button" className="btn btn-outline" onClick={() => copy(link.link ?? '')}>
              {copied ? 'Copiado' : 'Copiar'}
            </button>
          </div>
          <p className="muted">Por seguridad no se vuelve a mostrar: si se pierde, genere otro.</p>
        </>
      )}
    </Modal>
  );
}

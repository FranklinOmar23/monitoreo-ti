import { useState, type FormEvent } from 'react';
import { Pager } from '@components/Pager';
import { usePaged } from '../data/usePaged';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { PageHeader } from '@components/PageHeader';
import { initials } from '@components/Topbar';
import { usePanelApi } from '../data/PanelData';
import { dateTime, timeAgo } from '../data/format';

interface PerfilPageProps {
  companyName: string;
  roleName: string;
  /** El nombre cambió: la barra superior y la sesión lo muestran al renovarse; aquí se avisa para refrescarlo ya. */
  onRenamed?: () => void;
}

const MIN_PASSWORD = 10;

/** W-41 Mi perfil: datos, contraseña, verificación en dos pasos, avisos por correo y sesiones abiertas. */
export function PerfilPage({ companyName, roleName, onRenamed }: PerfilPageProps) {
  const api = usePanelApi();
  const profile = useQuery({ queryKey: ['perfil'], queryFn: () => api.usuarios.profile() });

  if (profile.isPending) return <Loading />;
  if (profile.isError) return <ErrorState error={profile.error} onRetry={() => void profile.refetch()} />;
  const me = profile.data;

  return (
    <div className="page">
      <PageHeader title="Mi perfil" subtitle={`${me.email} · ${roleName} en ${companyName}`} />
      <div className="profile-grid">
        <Card title="Datos">
          <div className="profile-head">
            <span className="avatar avatar-lg" aria-hidden="true">
              {initials(me.name)}
            </span>
            <span>
              <strong>{me.name}</strong>
              <span className="muted">Usuario desde el {dateTime(me.createdAt)}</span>
            </span>
          </div>
          <NameForm current={me.name} onRenamed={onRenamed} />
          <p className="muted">El correo lo cambia un administrador de la empresa.</p>
        </Card>

        <Card title="Contraseña">
          <PasswordForm />
        </Card>

        <Card title="Verificación en dos pasos">
          {me.twoFactorEnabled ? (
            <p className="profile-status">
              <span className="status-badge" data-status="sano">
                <Icon name="shield" size={14} /> Activa
              </span>
              <span className="muted">
                {me.lastTwoFactorAt ? `Último código: ${timeAgo(me.lastTwoFactorAt)}.` : ''} Si cambia de teléfono, pida
                a un administrador que la reinicie.
              </span>
            </p>
          ) : (
            <p className="profile-status">
              <span className="status-badge" data-status="desconectado">
                No activada
              </span>
              <span className="muted">Su rol no la exige. Si le dan acceso remoto, la activará al iniciar sesión.</span>
            </p>
          )}
        </Card>

        <Card title="Avisos por correo">
          <EmailPreference />
        </Card>

        <div className="form-span">
          <Card title="Sesiones abiertas">
            <OpenSessions />
          </Card>
        </div>
      </div>
    </div>
  );
}

function NameForm({ current, onRenamed }: { current: string; onRenamed?: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [name, setName] = useState(current);
  const save = useMutation({
    mutationFn: () => api.usuarios.rename(name.trim()),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['perfil'] });
      onRenamed?.();
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <form className="profile-form" onSubmit={submit}>
      {save.isError && (
        <p className="auth-error" role="alert">
          {save.error.message}
        </p>
      )}
      <label className="field">
        <span>Nombre</span>
        <input required maxLength={150} value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <button
        type="submit"
        className="btn btn-outline"
        disabled={save.isPending || !name.trim() || name.trim() === current}
      >
        {save.isSuccess && name.trim() === current ? 'Guardado' : 'Guardar nombre'}
      </button>
    </form>
  );
}

function PasswordForm() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [form, setForm] = useState({ actual: '', nueva: '', repetir: '' });
  const [problem, setProblem] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: () => api.usuarios.changePassword(form.actual, form.nueva),
    onSuccess: () => {
      setForm({ actual: '', nueva: '', repetir: '' });
      void queries.invalidateQueries({ queryKey: ['mis-sesiones'] });
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const problem =
      form.nueva.length < MIN_PASSWORD || !/\p{L}/u.test(form.nueva) || !/\d/.test(form.nueva)
        ? `La contraseña debe tener al menos ${MIN_PASSWORD} caracteres, con letras y números.`
        : form.nueva !== form.repetir
          ? 'Las contraseñas nuevas no coinciden.'
          : null;
    setProblem(problem);
    if (!problem) save.mutate();
  };

  const error = problem ?? (save.isError ? save.error.message : null);
  return (
    <form className="profile-form" onSubmit={submit}>
      {error && (
        <p className="auth-error" role="alert">
          {error}
        </p>
      )}
      {save.isSuccess && (
        <p className="auth-info" role="status">
          Contraseña cambiada. Se cerraron sus otras sesiones.
        </p>
      )}
      <label className="field">
        <span>Contraseña actual</span>
        <input
          type="password"
          required
          autoComplete="current-password"
          value={form.actual}
          onChange={(e) => setForm({ ...form, actual: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Contraseña nueva</span>
        <input
          type="password"
          required
          autoComplete="new-password"
          value={form.nueva}
          onChange={(e) => setForm({ ...form, nueva: e.target.value })}
        />
        <small className="muted">Al menos {MIN_PASSWORD} caracteres, con letras y números.</small>
      </label>
      <label className="field">
        <span>Repetir la contraseña nueva</span>
        <input
          type="password"
          required
          autoComplete="new-password"
          value={form.repetir}
          onChange={(e) => setForm({ ...form, repetir: e.target.value })}
        />
      </label>
      <button type="submit" className="btn btn-primary" disabled={save.isPending}>
        {save.isPending ? 'Cambiando…' : 'Cambiar contraseña'}
      </button>
    </form>
  );
}

function EmailPreference() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const prefs = useQuery({ queryKey: ['preferencias'], queryFn: () => api.preferences() });
  const save = useMutation({
    mutationFn: (notificarCorreo: boolean) => api.savePreferences({ notificarCorreo }),
    onSuccess: (data) => queries.setQueryData(['preferencias'], data),
  });

  return (
    <label className="check">
      <input
        type="checkbox"
        checked={prefs.data?.notificarCorreo ?? false}
        disabled={!prefs.data || save.isPending}
        onChange={(e) => save.mutate(e.target.checked)}
      />
      Recibir por correo las alertas críticas de lo que gestiono
    </label>
  );
}

function OpenSessions() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const { query: sessions, data: page, items, setPage } = usePaged(['mis-sesiones'], (p) => api.usuarios.sessions(p));
  const refresh = () => void queries.invalidateQueries({ queryKey: ['mis-sesiones'] });
  const close = useMutation({ mutationFn: (id: number) => api.usuarios.closeSession(id), onSuccess: refresh });
  const closeOthers = useMutation({ mutationFn: () => api.usuarios.closeOtherSessions(), onSuccess: refresh });

  if (sessions.isPending) return <Loading />;
  if (sessions.isError) return <ErrorState error={sessions.error} onRetry={() => void sessions.refetch()} />;
  // Todas menos la actual (que siempre existe).
  const others = Math.max(0, (page?.total ?? 0) - 1);
  const error = close.error ?? closeOthers.error;

  return (
    <>
      <p className="muted">
        Cada vez que inicia sesión en un equipo queda una sesión abierta. Si no reconoce alguna, ciérrela y cambie su
        contraseña. Una sesión cerrada deja de funcionar en 15 minutos como mucho.
      </p>
      {error && (
        <p className="auth-error" role="alert">
          {error.message}
        </p>
      )}
      <table className="data-table data-table-compact">
        <thead>
          <tr>
            <th>Equipo</th>
            <th>IP</th>
            <th>Inició</th>
            <th>Vence si no se usa</th>
            <th aria-label="Acciones" />
          </tr>
        </thead>
        <tbody>
          {items.map((s) => (
            <tr key={s.id}>
              <td>
                <Icon name="laptop" size={16} /> {s.device ?? 'Desconocido'}
                {s.current && <span className="chip chip-current">Esta sesión</span>}
              </td>
              <td className="mono">{s.ip ?? '—'}</td>
              <td title={dateTime(s.startedAt)}>{timeAgo(s.startedAt)}</td>
              <td>{dateTime(s.expiresAt)}</td>
              <td className="row-actions">
                {!s.current && (
                  <button
                    type="button"
                    className="btn btn-outline"
                    disabled={close.isPending}
                    onClick={() => close.mutate(s.id)}
                  >
                    Cerrar
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <Pager data={page} noun="sesiones" onPage={setPage} />
      {others > 0 && (
        <button
          type="button"
          className="btn btn-outline profile-close-others"
          disabled={closeOthers.isPending}
          onClick={() => closeOthers.mutate()}
        >
          <Icon name="logout" size={16} /> Cerrar las otras {others} {others === 1 ? 'sesión' : 'sesiones'}
        </button>
      )}
    </>
  );
}

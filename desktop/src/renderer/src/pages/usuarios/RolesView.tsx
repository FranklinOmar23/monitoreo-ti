import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { PermissionItem, RoleItem } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../../data/PanelData';

/** Lo que se abre: un rol existente (ver o editar), uno nuevo (vacío o copiado de otro). */
type RoleTarget = { role: RoleItem } | { copyOf?: RoleItem };

/**
 * W-38 Roles y permisos: matriz de permisos por rol. Los roles del sistema se ven y se duplican; los de la empresa
 * se editan y se eliminan si nadie los usa.
 */
export function RolesView({ creating, onCreatingDone }: { creating: boolean; onCreatingDone: () => void }) {
  const api = usePanelApi();
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api.usuarios.roles() });
  const permissions = useQuery({ queryKey: ['permisos'], queryFn: () => api.usuarios.permissions() });
  const [open, setOpen] = useState<RoleTarget | null>(null);
  const target = creating ? {} : open;
  const close = () => {
    setOpen(null);
    onCreatingDone();
  };

  if (roles.isPending || permissions.isPending) return <Loading />;
  if (roles.isError) return <ErrorState error={roles.error} onRetry={() => void roles.refetch()} />;
  if (permissions.isError) return <ErrorState error={permissions.error} onRetry={() => void permissions.refetch()} />;

  const groups = groupPermissions(permissions.data);

  return (
    <>
      <p className="muted roles-intro">
        Un rol agrupa permisos. Los roles del sistema no se cambian: duplíquelos para crear uno a su medida. Los cambios
        llegan a los usuarios en unos 15 minutos o al volver a iniciar sesión.
      </p>
      <div className="table-card roles-matrix-wrap">
        <table className="data-table roles-matrix">
          <thead>
            <tr>
              <th>Permiso</th>
              {roles.data.map((r) => (
                <th key={r.id} scope="col">
                  <button type="button" className="role-head" onClick={() => setOpen({ role: r })}>
                    <strong>{r.name}</strong>
                    <span className="muted">
                      {r.isSystem ? 'Sistema' : 'Propio'} · {r.userCount} {r.userCount === 1 ? 'usuario' : 'usuarios'}
                    </span>
                  </button>
                </th>
              ))}
            </tr>
          </thead>
          {groups.map(([group, items]) => (
            <tbody key={group}>
              <tr className="roles-group">
                <th colSpan={roles.data.length + 1} scope="colgroup">
                  {group}
                </th>
              </tr>
              {items.map((p) => (
                <tr key={p.key}>
                  <th scope="row" className="perm-name">
                    {p.description}
                    {p.requiresTwoFactor && <TwoFactorTag />}
                  </th>
                  {roles.data.map((r) => (
                    <td key={r.id} className="perm-cell">
                      {r.permissions.includes(p.key) ? (
                        <span className="perm-yes" role="img" aria-label={`${r.name}: ${p.description}`}>
                          <Icon name="check" size={16} />
                        </span>
                      ) : (
                        <span className="perm-none" role="img" aria-label={`${r.name}: sin ${p.description}`}>
                          —
                        </span>
                      )}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          ))}
        </table>
      </div>

      {target && (
        <RoleForm
          key={'role' in target ? target.role.id : `nuevo-${target.copyOf?.id ?? ''}`}
          target={target}
          permissions={permissions.data}
          onClose={close}
          onDuplicate={(role) => setOpen({ copyOf: role })}
        />
      )}
    </>
  );
}

function TwoFactorTag() {
  return (
    <span className="tag-2fa" title="Da control de equipos ajenos: exige la verificación en dos pasos">
      <Icon name="shield" size={12} /> 2FA
    </span>
  );
}

function groupPermissions(items: PermissionItem[]): [string, PermissionItem[]][] {
  const map = new Map<string, PermissionItem[]>();
  for (const item of items) map.set(item.group, [...(map.get(item.group) ?? []), item]);
  return [...map.entries()];
}

function RoleForm({
  target,
  permissions,
  onClose,
  onDuplicate,
}: {
  target: RoleTarget;
  permissions: PermissionItem[];
  onClose: () => void;
  onDuplicate: (role: RoleItem) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const role = 'role' in target ? target.role : undefined;
  const source = role ?? ('copyOf' in target ? target.copyOf : undefined);
  const readOnly = role?.isSystem ?? false;
  const [form, setForm] = useState({
    nombre: role ? role.name : source ? `${source.name} (copia)` : '',
    descripcion: source?.description ?? '',
    permisos: new Set(source?.permissions ?? []),
  });

  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['roles'] });
    void queries.invalidateQueries({ queryKey: ['usuarios'] });
  };
  const save = useMutation({
    mutationFn: () =>
      api.usuarios.saveRole(role?.id ?? null, {
        nombre: form.nombre.trim(),
        descripcion: form.descripcion.trim() || null,
        permisos: [...form.permisos],
      }),
    onSuccess: () => {
      refresh();
      onClose();
    },
  });
  const remove = useMutation({
    mutationFn: () => api.usuarios.deleteRole(role!.id),
    onSuccess: () => {
      refresh();
      onClose();
    },
  });

  const toggle = (key: string) => {
    const next = new Set(form.permisos);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    setForm({ ...form, permisos: next });
  };
  const remote = permissions.some((p) => p.requiresTwoFactor && form.permisos.has(p.key));
  const error = save.error ?? remove.error;

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={readOnly ? `Rol ${role!.name}` : role ? `Editar ${role.name}` : 'Nuevo rol'}
      onClose={onClose}
      wide
      footer={
        readOnly ? (
          <>
            <button type="button" className="btn btn-outline" onClick={onClose}>
              Cerrar
            </button>
            <button type="button" className="btn btn-primary" onClick={() => onDuplicate(role!)}>
              Duplicar
            </button>
          </>
        ) : (
          <>
            {role && (
              <button
                type="button"
                className="btn btn-danger modal-footer-start"
                disabled={remove.isPending}
                onClick={() => {
                  if (window.confirm(`¿Eliminar el rol ${role.name}?`)) remove.mutate();
                }}
              >
                Eliminar
              </button>
            )}
            {role && (
              <button type="button" className="btn btn-outline" onClick={() => onDuplicate(role)}>
                Duplicar
              </button>
            )}
            <button type="button" className="btn btn-outline" onClick={onClose}>
              Cancelar
            </button>
            <button type="submit" form="role-form" className="btn btn-primary" disabled={save.isPending}>
              {save.isPending ? 'Guardando…' : 'Guardar'}
            </button>
          </>
        )
      }
    >
      <form id="role-form" className="form-grid" onSubmit={submit}>
        {error && (
          <p className="auth-error form-span" role="alert">
            {error.message}
          </p>
        )}
        {readOnly && (
          <p className="notice form-span">
            <Icon name="lock" size={16} /> Rol del sistema: no se cambia. Duplíquelo para crear uno a su medida.
          </p>
        )}
        <label className="field">
          <span>Nombre *</span>
          <input
            required
            maxLength={50}
            value={form.nombre}
            disabled={readOnly}
            onChange={(e) => setForm({ ...form, nombre: e.target.value })}
            autoFocus={!readOnly}
          />
        </label>
        <label className="field">
          <span>Descripción</span>
          <input
            maxLength={200}
            value={form.descripcion}
            disabled={readOnly}
            onChange={(e) => setForm({ ...form, descripcion: e.target.value })}
          />
        </label>

        <div className="form-span role-permissions">
          {groupPermissions(permissions).map(([group, items]) => (
            <fieldset key={group}>
              <legend>{group}</legend>
              {items.map((p) => (
                <label key={p.key} className="check">
                  <input
                    type="checkbox"
                    checked={form.permisos.has(p.key)}
                    disabled={readOnly}
                    onChange={() => toggle(p.key)}
                  />
                  <span>
                    {p.description}
                    {p.requiresTwoFactor && <TwoFactorTag />}
                  </span>
                </label>
              ))}
            </fieldset>
          ))}
        </div>
        {remote && !readOnly && (
          <p className="notice form-span">
            <Icon name="shield" size={16} /> Este rol da acceso remoto: sus usuarios deberán activar la verificación en
            dos pasos al iniciar sesión.
          </p>
        )}
      </form>
    </Modal>
  );
}

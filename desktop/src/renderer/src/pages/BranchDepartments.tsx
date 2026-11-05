import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { BranchItem, DepartmentItem } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../data/PanelData';

/**
 * Departamentos de una sucursal (decisión 79): cada uno con su responsable y sus equipos. Se crean, renombran y
 * eliminan aquí (sucursales.editar); uno con equipos no se elimina: primero se pasan a otro.
 */
export function BranchDepartments({
  branch,
  canEdit,
  onOpenMachines,
}: {
  branch: BranchItem;
  canEdit: boolean;
  onOpenMachines: (departmentId: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const list = useQuery({
    queryKey: ['sucursales', branch.id, 'departamentos'],
    queryFn: () => api.branchDepartments(branch.id),
  });
  const [editing, setEditing] = useState<DepartmentItem | 'new' | null>(null);
  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['sucursales', branch.id, 'departamentos'] });
    // El formulario del equipo y Configuración los listan.
    void queries.invalidateQueries({ queryKey: ['departamentos'] });
    void queries.invalidateQueries({ queryKey: ['configuracion', 'departamentos'] });
  };
  const remove = useMutation({
    mutationFn: (id: number) => api.deleteDepartment(branch.id, id),
    onSuccess: refresh,
  });

  return (
    <Card
      title="Departamentos"
      aside={
        canEdit && (
          <button type="button" className="btn btn-outline" onClick={() => setEditing('new')}>
            <Icon name="plus" size={16} /> Agregar departamento
          </button>
        )
      }
    >
      {remove.isError && (
        <p className="auth-error" role="alert">
          {remove.error.message}
        </p>
      )}
      {list.isPending ? (
        <Loading />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : list.data.length === 0 ? (
        <EmptyState title={`${branch.name} todavía no tiene departamentos`} />
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Departamento</th>
              <th>Responsable</th>
              <th>Equipos</th>
              {canEdit && <th aria-label="Acciones" />}
            </tr>
          </thead>
          <tbody>
            {list.data.map((d) => (
              <tr key={d.id}>
                <td>{d.name}</td>
                <td>{d.responsibleName ?? '—'}</td>
                <td>
                  {d.machineCount > 0 ? (
                    <button
                      type="button"
                      className="link-quiet mono"
                      title={`Ver los equipos de ${d.name}`}
                      onClick={() => onOpenMachines(d.id)}
                    >
                      {d.machineCount}
                    </button>
                  ) : (
                    <span className="mono muted">0</span>
                  )}
                </td>
                {canEdit && (
                  <td className="row-actions">
                    <button type="button" className="btn btn-outline" onClick={() => setEditing(d)}>
                      Editar
                    </button>
                    <button
                      type="button"
                      className="btn btn-outline"
                      disabled={remove.isPending || d.machineCount > 0}
                      title={
                        d.machineCount > 0
                          ? 'Tiene equipos: páselos a otro departamento antes de eliminarlo'
                          : undefined
                      }
                      onClick={() => {
                        if (window.confirm(`¿Eliminar el departamento ${d.name} de ${branch.name}?`))
                          remove.mutate(d.id);
                      }}
                    >
                      Eliminar
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && (
        <DepartmentForm
          branch={branch}
          department={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            refresh();
            setEditing(null);
          }}
        />
      )}
    </Card>
  );
}

function DepartmentForm({
  branch,
  department,
  onClose,
  onSaved,
}: {
  branch: BranchItem;
  department: DepartmentItem | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const api = usePanelApi();
  const users = useQuery({ queryKey: ['usuarios-empresa'], queryFn: () => api.companyUsers() });
  const [name, setName] = useState(department?.name ?? '');
  const [responsible, setResponsible] = useState(department?.responsibleId ? String(department.responsibleId) : '');
  const save = useMutation({
    mutationFn: () =>
      api.saveDepartment(branch.id, department?.id ?? null, {
        nombre: name.trim(),
        responsableId: responsible ? Number(responsible) : null,
      }),
    onSuccess: onSaved,
  });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <Modal
      title={department ? `Editar ${department.name}` : `Nuevo departamento en ${branch.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button
            type="submit"
            form="department-form"
            className="btn btn-primary"
            disabled={save.isPending || !name.trim()}
          >
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
        </>
      }
    >
      <form id="department-form" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field">
          <span>Nombre *</span>
          <input
            required
            autoFocus
            maxLength={100}
            value={name}
            placeholder="Contabilidad"
            onChange={(e) => setName(e.target.value)}
          />
        </label>
        <label className="field">
          <span>Responsable</span>
          <select value={responsible} onChange={(e) => setResponsible(e.target.value)}>
            <option value="">Sin responsable</option>
            {(users.data ?? []).map((u) => (
              <option key={u.id} value={u.id}>
                {u.nombre}
              </option>
            ))}
          </select>
        </label>
      </form>
    </Modal>
  );
}

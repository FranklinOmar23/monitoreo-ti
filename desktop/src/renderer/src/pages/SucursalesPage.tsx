import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { BranchItem } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { usePanelApi } from '../data/PanelData';
import { StatusBar, worst } from './DashboardPage';

interface SucursalesPageProps {
  canEdit: boolean;
  onOpenMachines: (branchId: number) => void;
}

/** W-15 Sucursales (lista), W-16 detalle y W-17 formulario. */
export function SucursalesPage({ canEdit, onOpenMachines }: SucursalesPageProps) {
  const api = usePanelApi();
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const [editing, setEditing] = useState<BranchItem | 'new' | null>(null);
  const [selected, setSelected] = useState<number | null>(null);

  const detail = branches.data?.find((b) => b.id === selected);

  return (
    <>
      <PageHeader
        title="Sucursales"
        subtitle={branches.data ? `${branches.data.length} sucursales` : 'Cargando…'}
        actions={
          canEdit && (
            <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>
              Agregar sucursal
            </button>
          )
        }
      />

      {branches.isPending ? (
        <Loading />
      ) : branches.isError ? (
        <ErrorState error={branches.error} onRetry={() => void branches.refetch()} />
      ) : branches.data.length === 0 ? (
        <EmptyState title="Todavía no hay sucursales" />
      ) : detail ? (
        <BranchDetail
          branch={detail}
          canEdit={canEdit}
          onBack={() => setSelected(null)}
          onEdit={() => setEditing(detail)}
          onOpenMachines={() => onOpenMachines(detail.id)}
        />
      ) : (
        <div className="branch-grid">
          {branches.data.map((branch) => (
            <button key={branch.id} type="button" className="card branch-card" onClick={() => setSelected(branch.id)}>
              <span className="branch-name">
                <span className="dot" data-status={worst(branch.counts)} aria-hidden="true" />
                {branch.name}
                {!branch.isActive && <span className="chip">Inactiva</span>}
              </span>
              <span className="muted">{branch.province ?? 'Sin provincia'}</span>
              <span className="branch-meta mono">
                {branch.counts.total} equipos · {branch.counts.critical} crít. · {branch.counts.warning} adv. ·{' '}
                {branch.counts.disconnected} descon.
              </span>
              <StatusBar counts={branch.counts} label={`Estado de ${branch.name}`} />
            </button>
          ))}
        </div>
      )}

      {editing && (
        <BranchForm
          branch={editing === 'new' ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(id) => {
            setEditing(null);
            setSelected(id);
          }}
        />
      )}
    </>
  );
}

function BranchDetail({
  branch,
  canEdit,
  onBack,
  onEdit,
  onOpenMachines,
}: {
  branch: BranchItem;
  canEdit: boolean;
  onBack: () => void;
  onEdit: () => void;
  onOpenMachines: () => void;
}) {
  const { counts } = branch;
  return (
    <>
      <nav className="breadcrumb" aria-label="Ruta">
        <button type="button" className="link-quiet" onClick={onBack}>
          Sucursales
        </button>
        <span aria-hidden="true">›</span>
        <strong>{branch.name}</strong>
      </nav>
      <div className="ficha-grid">
        <Card
          title={branch.name}
          aside={
            canEdit && (
              <button type="button" className="btn btn-outline" onClick={onEdit}>
                Editar
              </button>
            )
          }
        >
          <dl className="info-list">
            <div>
              <dt>Provincia</dt>
              <dd>{branch.province ?? '—'}</dd>
            </div>
            <div>
              <dt>Dirección</dt>
              <dd>{branch.address ?? '—'}</dd>
            </div>
            <div>
              <dt>Responsable</dt>
              <dd>{branch.managerName ?? '—'}</dd>
            </div>
            <div>
              <dt>Estado</dt>
              <dd>{branch.isActive ? 'Activa' : 'Inactiva'}</dd>
            </div>
          </dl>
        </Card>
        <Card title="Equipos">
          <ul className="branch-counts">
            <li data-status="sano">
              <strong>{counts.healthy}</strong> sanos
            </li>
            <li data-status="advertencia">
              <strong>{counts.warning}</strong> en advertencia
            </li>
            <li data-status="critico">
              <strong>{counts.critical}</strong> críticos
            </li>
            <li data-status="desconectado">
              <strong>{counts.disconnected}</strong> desconectados
            </li>
          </ul>
          <StatusBar counts={counts} label={`Estado de ${branch.name}`} />
          <button type="button" className="btn btn-primary branch-machines" onClick={onOpenMachines}>
            <Icon name="monitor" size={16} /> Ver sus {counts.total} equipos
          </button>
        </Card>
      </div>
    </>
  );
}

function BranchForm({
  branch,
  onClose,
  onSaved,
}: {
  branch?: BranchItem;
  onClose: () => void;
  onSaved: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const users = useQuery({ queryKey: ['usuarios-empresa'], queryFn: () => api.companyUsers() });
  const [form, setForm] = useState({
    nombre: branch?.name ?? '',
    provincia: branch?.province ?? '',
    direccion: branch?.address ?? '',
    responsableId: branch?.managerId ? String(branch.managerId) : '',
    activa: branch?.isActive ?? true,
  });

  const save = useMutation({
    mutationFn: () => {
      const input = {
        nombre: form.nombre.trim(),
        provincia: form.provincia.trim() || null,
        direccion: form.direccion.trim() || null,
        responsableId: form.responsableId ? Number(form.responsableId) : null,
        activa: form.activa,
      };
      return branch ? api.updateBranch(branch.id, input) : api.createBranch(input);
    },
    onSuccess: ({ id }) => {
      void queries.invalidateQueries({ queryKey: ['sucursales'] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
      onSaved(id);
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={branch ? `Editar ${branch.name}` : 'Agregar sucursal'}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="branch-form" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
        </>
      }
    >
      <form id="branch-form" className="form-grid form-grid-single" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field">
          <span>Nombre *</span>
          <input
            required
            maxLength={100}
            value={form.nombre}
            onChange={(e) => setForm({ ...form, nombre: e.target.value })}
            autoFocus
          />
        </label>
        <label className="field">
          <span>Provincia</span>
          <input
            maxLength={100}
            value={form.provincia}
            onChange={(e) => setForm({ ...form, provincia: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Dirección</span>
          <input
            maxLength={300}
            value={form.direccion}
            onChange={(e) => setForm({ ...form, direccion: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Responsable</span>
          <select value={form.responsableId} onChange={(e) => setForm({ ...form, responsableId: e.target.value })}>
            <option value="">Sin responsable</option>
            {(users.data ?? []).map((u) => (
              <option key={u.id} value={u.id}>
                {u.nombre}
              </option>
            ))}
          </select>
        </label>
        {branch && (
          <label className="check">
            <input
              type="checkbox"
              checked={form.activa}
              onChange={(e) => setForm({ ...form, activa: e.target.checked })}
            />
            Sucursal activa
          </label>
        )}
      </form>
    </Modal>
  );
}

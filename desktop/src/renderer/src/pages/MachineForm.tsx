import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { MachineDetail, MachineInput, MachineType } from '@monitoreoti/shared';
import { Modal } from '@components/Modal';
import { MACHINE_TYPES, typeLabel } from '../data/format';
import { usePanelApi } from '../data/PanelData';

interface MachineFormProps {
  /** Equipo a editar; sin él, se agrega uno nuevo. */
  machine?: MachineDetail;
  onClose: () => void;
  onSaved: (id: number) => void;
}

const empty = (value: string) => (value.trim() === '' ? null : value.trim());

/** W-13 Agregar o editar equipo manualmente (impresoras, equipos sin agente, datos de compra y garantía). */
export function MachineForm({ machine, onClose, onSaved }: MachineFormProps) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const departments = useQuery({ queryKey: ['departamentos'], queryFn: () => api.departments() });

  const [form, setForm] = useState({
    nombre: machine?.name ?? '',
    tipo: (machine?.type ?? 'computadora') as MachineType,
    sucursalId: machine ? String(machine.branchId) : '',
    departamentoId: machine?.departmentId ? String(machine.departmentId) : '',
    marca: machine?.brand ?? '',
    modelo: machine?.model ?? '',
    serie: machine?.serialNumber ?? '',
    sistemaOperativo: machine?.operatingSystem ?? '',
    ip: machine?.ipAddress ?? '',
    mac: machine?.macAddress ?? '',
    responsable: machine?.assignedTo ?? '',
    fechaCompra: machine?.purchaseDate ?? '',
    garantiaHasta: machine?.warrantyUntil ?? '',
  });
  const set = (key: keyof typeof form) => (event: { target: { value: string } }) =>
    setForm((current) => ({ ...current, [key]: event.target.value }));

  const save = useMutation({
    mutationFn: () => {
      const input: MachineInput = {
        nombre: form.nombre.trim(),
        tipo: form.tipo,
        sucursalId: Number(form.sucursalId),
        departamentoId: form.departamentoId ? Number(form.departamentoId) : null,
        marca: empty(form.marca),
        modelo: empty(form.modelo),
        serie: empty(form.serie),
        sistemaOperativo: empty(form.sistemaOperativo),
        ip: empty(form.ip),
        mac: empty(form.mac),
        responsable: empty(form.responsable),
        fechaCompra: empty(form.fechaCompra),
        garantiaHasta: empty(form.garantiaHasta),
      };
      return machine ? api.updateMachine(machine.id, input) : api.createMachine(input);
    },
    onSuccess: ({ id }) => {
      void queries.invalidateQueries({ queryKey: ['maquinas'] });
      void queries.invalidateQueries({ queryKey: ['maquina', id] });
      void queries.invalidateQueries({ queryKey: ['historial', id] });
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
      title={machine ? `Editar ${machine.name}` : 'Agregar equipo'}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="machine-form" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
        </>
      }
    >
      <form id="machine-form" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field">
          <span>Nombre *</span>
          <input required maxLength={100} value={form.nombre} onChange={set('nombre')} autoFocus />
        </label>
        <label className="field">
          <span>Tipo *</span>
          <select value={form.tipo} onChange={set('tipo')}>
            {MACHINE_TYPES.map((type) => (
              <option key={type} value={type}>
                {typeLabel(type)}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Sucursal *</span>
          <select required value={form.sucursalId} onChange={set('sucursalId')}>
            <option value="">Elija la sucursal</option>
            {(branches.data ?? []).map((branch) => (
              <option key={branch.id} value={branch.id}>
                {branch.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Departamento</span>
          <select value={form.departamentoId} onChange={set('departamentoId')}>
            <option value="">Sin departamento</option>
            {(departments.data ?? []).map((d) => (
              <option key={d.id} value={d.id}>
                {d.nombre}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Marca</span>
          <input maxLength={100} value={form.marca} onChange={set('marca')} />
        </label>
        <label className="field">
          <span>Modelo</span>
          <input maxLength={100} value={form.modelo} onChange={set('modelo')} />
        </label>
        <label className="field">
          <span>Número de serie</span>
          <input maxLength={100} value={form.serie} onChange={set('serie')} />
        </label>
        <label className="field">
          <span>Sistema operativo</span>
          <input maxLength={150} value={form.sistemaOperativo} onChange={set('sistemaOperativo')} />
        </label>
        <label className="field">
          <span>IP</span>
          <input className="mono" maxLength={45} value={form.ip} onChange={set('ip')} placeholder="10.10.20.31" />
        </label>
        <label className="field">
          <span>MAC</span>
          <input
            className="mono"
            maxLength={17}
            value={form.mac}
            onChange={set('mac')}
            placeholder="AA:BB:CC:DD:EE:FF"
          />
        </label>
        <label className="field form-span">
          <span>Responsable</span>
          <input
            maxLength={150}
            value={form.responsable}
            onChange={set('responsable')}
            placeholder="Quién usa el equipo"
          />
        </label>
        <label className="field">
          <span>Fecha de compra</span>
          <input type="date" value={form.fechaCompra} onChange={set('fechaCompra')} />
        </label>
        <label className="field">
          <span>Garantía hasta</span>
          <input type="date" value={form.garantiaHasta} onChange={set('garantiaHasta')} />
        </label>
      </form>
    </Modal>
  );
}

interface RetireDialogProps {
  machine: MachineDetail;
  onClose: () => void;
  onRetired: () => void;
}

/** Confirmación para dar de baja (el equipo no se borra: conserva su historial). */
export function RetireDialog({ machine, onClose, onRetired }: RetireDialogProps) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const retire = useMutation({
    mutationFn: () => api.retireMachine(machine.id),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['maquinas'] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
      onRetired();
    },
  });

  return (
    <Modal
      title={`Dar de baja ${machine.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="button" className="btn btn-ticket" disabled={retire.isPending} onClick={() => retire.mutate()}>
            {retire.isPending ? 'Dando de baja…' : 'Dar de baja'}
          </button>
        </>
      }
    >
      {retire.isError && (
        <p className="auth-error" role="alert">
          {retire.error.message}
        </p>
      )}
      <p>
        El equipo deja de aparecer en las listas y su agente ya no podrá conectarse. Su historial (métricas, errores y
        cambios) se conserva.
      </p>
    </Modal>
  );
}

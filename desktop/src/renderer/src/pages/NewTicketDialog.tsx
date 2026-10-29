import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { TicketPriority } from '@monitoreoti/shared';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../data/PanelData';

const PRIORITIES: { value: TicketPriority; label: string }[] = [
  { value: 'baja', label: 'Baja' },
  { value: 'media', label: 'Media' },
  { value: 'alta', label: 'Alta' },
  { value: 'urgente', label: 'Urgente' },
];

interface NewTicketDialogProps {
  /** Equipo del ticket (desde la ficha). */
  machineId?: number;
  machineName?: string;
  /** Alerta de origen: el servidor toma el equipo de la alerta y la marca "en proceso". */
  alertId?: number;
  defaultTitle?: string;
  defaultDescription?: string;
  defaultPriority?: TicketPriority;
  /** Quien gestiona tickets puede asignarlo al crearlo. */
  canAssign: boolean;
  onClose: () => void;
  onCreated: (id: number) => void;
}

/** Crear ticket: desde la ficha (W-06), desde una alerta (W-21) o desde la pantalla de tickets. */
export function NewTicketDialog({
  machineId,
  machineName,
  alertId,
  defaultTitle = '',
  defaultDescription = '',
  defaultPriority = 'media',
  canAssign,
  onClose,
  onCreated,
}: NewTicketDialogProps) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const technicians = useQuery({ queryKey: ['tecnicos'], queryFn: () => api.technicians(), enabled: canAssign });
  const [form, setForm] = useState({
    titulo: defaultTitle,
    descripcion: defaultDescription,
    prioridad: defaultPriority,
    asignadoA: '',
  });

  const save = useMutation({
    mutationFn: () =>
      api.createTicket({
        titulo: form.titulo.trim(),
        descripcion: form.descripcion.trim() || null,
        prioridad: form.prioridad,
        maquinaId: alertId ? null : (machineId ?? null),
        alertaId: alertId ?? null,
        asignadoA: form.asignadoA ? Number(form.asignadoA) : null,
      }),
    onSuccess: ({ id }) => {
      void queries.invalidateQueries({ queryKey: ['tickets'] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
      if (alertId) {
        void queries.invalidateQueries({ queryKey: ['alerta', alertId] });
        void queries.invalidateQueries({ queryKey: ['alertas'] });
      }
      onCreated(id);
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <Modal
      title={machineName ? `Crear ticket · ${machineName}` : 'Crear ticket'}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="new-ticket" className="btn btn-ticket" disabled={save.isPending}>
            {save.isPending ? 'Creando…' : 'Crear ticket'}
          </button>
        </>
      }
    >
      <form id="new-ticket" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field form-span">
          <span>Título *</span>
          <input
            required
            maxLength={200}
            value={form.titulo}
            onChange={(e) => setForm({ ...form, titulo: e.target.value })}
            autoFocus
          />
        </label>
        <label className="field form-span">
          <span>Descripción</span>
          <textarea
            rows={4}
            maxLength={4000}
            value={form.descripcion}
            placeholder="Qué pasa, desde cuándo, qué se probó"
            onChange={(e) => setForm({ ...form, descripcion: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Prioridad</span>
          <select
            value={form.prioridad}
            onChange={(e) => setForm({ ...form, prioridad: e.target.value as TicketPriority })}
          >
            {PRIORITIES.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
        </label>
        {canAssign && (
          <label className="field">
            <span>Asignar a</span>
            <select value={form.asignadoA} onChange={(e) => setForm({ ...form, asignadoA: e.target.value })}>
              <option value="">Sin asignar</option>
              {(technicians.data ?? []).map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </label>
        )}
      </form>
    </Modal>
  );
}

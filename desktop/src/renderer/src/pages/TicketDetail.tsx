import { useRef, useState, type ChangeEvent, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { TicketDetail, TicketPriority, TicketStatus } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { StatusBadge } from '@components/StatusBadge';
import { dateTime, timeAgo } from '../data/format';
import { usePanelApi } from '../data/PanelData';
import { PRIORITY_LABEL, TICKET_STATUS_LABEL } from './ticketLabels';

const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
const IMAGE_TYPES = ['image/jpeg', 'image/png', 'image/webp'];

interface TicketDetailViewProps {
  ticketId: number;
  canManage: boolean;
  onBack: () => void;
  onOpenEquipo: (id: number) => void;
  onOpenAlert: (id: number) => void;
}

/** W-26 Detalle de ticket: descripción, conversación, imágenes, estado, asignación e historial. */
export function TicketDetailView({ ticketId, canManage, onBack, onOpenEquipo, onOpenAlert }: TicketDetailViewProps) {
  const api = usePanelApi();
  const query = useQuery({ queryKey: ['ticket', ticketId], queryFn: () => api.ticket(ticketId) });

  return (
    <>
      <nav className="breadcrumb" aria-label="Ruta">
        <button type="button" className="link-quiet" onClick={onBack}>
          Tickets
        </button>
        <span aria-hidden="true">›</span>
        <strong className="mono">#{ticketId}</strong>
      </nav>
      {query.isPending ? (
        <Loading />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <TicketBody ticket={query.data} canManage={canManage} onOpenEquipo={onOpenEquipo} onOpenAlert={onOpenAlert} />
      )}
    </>
  );
}

/** Cambios del ticket: refrescan su detalle y las listas. */
function useTicketMutation<T>(ticketId: number, fn: (value: T) => Promise<unknown>) {
  const queries = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['ticket', ticketId] });
      void queries.invalidateQueries({ queryKey: ['tickets'] });
    },
  });
}

function TicketBody({
  ticket,
  canManage,
  onOpenEquipo,
  onOpenAlert,
}: {
  ticket: TicketDetail;
  canManage: boolean;
  onOpenEquipo: (id: number) => void;
  onOpenAlert: (id: number) => void;
}) {
  const api = usePanelApi();
  const [editing, setEditing] = useState(false);
  const technicians = useQuery({ queryKey: ['tecnicos'], queryFn: () => api.technicians(), enabled: canManage });
  const status = useTicketMutation(ticket.id, (value: TicketStatus) => api.setTicketStatus(ticket.id, value));
  const assign = useTicketMutation(ticket.id, (value: number | null) => api.assignTicket(ticket.id, value));
  const closed = ticket.status === 'cerrado';
  const error = status.error ?? assign.error;

  return (
    <div className="ficha-grid">
      <div className="ficha-main">
        <Card>
          <div className="alert-head">
            <span
              className="problem-card-icon"
              data-status={ticket.priority === 'urgente' || ticket.priority === 'alta' ? 'critico' : undefined}
            >
              <Icon name="ticket" />
            </span>
            <div>
              <h1 className="alert-title">{ticket.title}</h1>
              <div className="alert-meta">
                <span className="priority" data-priority={ticket.priority}>
                  {PRIORITY_LABEL[ticket.priority]}
                </span>
                <span className="ticket-status" data-ticket={ticket.status}>
                  {TICKET_STATUS_LABEL[ticket.status]}
                </span>
                <span className="muted">
                  Creado por {ticket.createdBy} · {dateTime(ticket.createdAt)}
                </span>
              </div>
            </div>
            {(canManage || (ticket.status === 'abierto' && !ticket.assignedToId)) && !closed && (
              <div className="ficha-actions">
                <button type="button" className="btn btn-outline" onClick={() => setEditing(true)}>
                  Editar
                </button>
              </div>
            )}
          </div>
          <p className="alert-detail ticket-description">{ticket.description ?? 'Sin descripción.'}</p>
        </Card>

        <Comments ticket={ticket} />
        <Attachments ticket={ticket} />
      </div>

      <div className="ficha-side">
        <Card title="Gestión">
          {error && (
            <p className="auth-error" role="alert">
              {error.message}
            </p>
          )}
          {canManage ? (
            <div className="form-grid form-grid-single">
              <label className="field">
                <span>Estado</span>
                <select
                  value={ticket.status}
                  disabled={status.isPending}
                  onChange={(e) => status.mutate(e.target.value as TicketStatus)}
                >
                  {(Object.keys(TICKET_STATUS_LABEL) as TicketStatus[]).map((s) => (
                    <option key={s} value={s}>
                      {TICKET_STATUS_LABEL[s]}
                    </option>
                  ))}
                </select>
              </label>
              <label className="field">
                <span>Asignado a</span>
                <select
                  value={ticket.assignedToId ?? ''}
                  disabled={assign.isPending}
                  onChange={(e) => assign.mutate(e.target.value ? Number(e.target.value) : null)}
                >
                  <option value="">Sin asignar</option>
                  {(technicians.data ?? []).map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name}
                    </option>
                  ))}
                </select>
              </label>
            </div>
          ) : (
            <dl className="info-list">
              <div>
                <dt>Estado</dt>
                <dd>{TICKET_STATUS_LABEL[ticket.status]}</dd>
              </div>
              <div>
                <dt>Asignado a</dt>
                <dd>{ticket.assignedTo ?? 'Sin asignar'}</dd>
              </div>
            </dl>
          )}
        </Card>

        <Card title="Origen">
          <dl className="info-list">
            <div>
              <dt>Equipo</dt>
              <dd>
                {ticket.machineId !== null ? (
                  <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(ticket.machineId!)}>
                    {ticket.machineName}
                  </button>
                ) : (
                  'Sin equipo'
                )}
              </dd>
            </div>
            <div>
              <dt>Estado del equipo</dt>
              <dd>{ticket.machineStatus ? <StatusBadge status={ticket.machineStatus} /> : '—'}</dd>
            </div>
            <div>
              <dt>Sucursal</dt>
              <dd>{ticket.branchName ?? '—'}</dd>
            </div>
            <div>
              <dt>Cerrado</dt>
              <dd>{ticket.closedAt ? dateTime(ticket.closedAt) : '—'}</dd>
            </div>
          </dl>
          {ticket.alertId !== null && (
            <button type="button" className="ticket-alert" onClick={() => onOpenAlert(ticket.alertId!)}>
              <Icon name="bell" size={14} />
              <span>
                <strong>Alerta #{ticket.alertId}</strong> {ticket.alertMessage}
                {ticket.alertStatus === 'resuelta' && ' (resuelta)'}
              </span>
            </button>
          )}
        </Card>

        <Card title="Historial">
          <ul className="timeline">
            {ticket.history.map((item, i) => (
              <li key={i}>
                <strong>{item.action}</strong>
                {item.detail && <span>{item.detail}</span>}
                <span className="muted">
                  {item.userName ?? 'Sistema'} · {dateTime(item.at)}
                </span>
              </li>
            ))}
          </ul>
        </Card>
      </div>
      {editing && <EditTicketDialog ticket={ticket} onClose={() => setEditing(false)} />}
    </div>
  );
}

function Comments({ ticket }: { ticket: TicketDetail }) {
  const api = usePanelApi();
  const [text, setText] = useState('');
  const comment = useTicketMutation(ticket.id, (value: string) => api.commentTicket(ticket.id, value));
  const closed = ticket.status === 'cerrado';

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!text.trim()) return;
    comment.mutate(text.trim(), { onSuccess: () => setText('') });
  };

  return (
    <Card title={`Conversación (${ticket.comments.length})`}>
      {ticket.comments.length === 0 ? (
        <p className="muted">Todavía no hay comentarios.</p>
      ) : (
        <ul className="comments">
          {ticket.comments.map((c) => (
            <li key={c.id}>
              <span className="comment-avatar" aria-hidden="true">
                {c.userName.slice(0, 1).toUpperCase()}
              </span>
              <div>
                <strong>{c.userName}</strong>
                <span className="muted" title={dateTime(c.at)}>
                  {' '}
                  · {timeAgo(c.at)}
                </span>
                <p>{c.text}</p>
              </div>
            </li>
          ))}
        </ul>
      )}
      {closed ? (
        <p className="muted note">El ticket está cerrado.</p>
      ) : (
        <form className="comment-form" onSubmit={submit}>
          {comment.isError && (
            <p className="auth-error" role="alert">
              {comment.error.message}
            </p>
          )}
          <label className="field">
            <span>Agregar comentario</span>
            <textarea rows={3} maxLength={4000} value={text} onChange={(e) => setText(e.target.value)} />
          </label>
          <button type="submit" className="btn btn-primary" disabled={comment.isPending || !text.trim()}>
            {comment.isPending ? 'Enviando…' : 'Comentar'}
          </button>
        </form>
      )}
    </Card>
  );
}

function Attachments({ ticket }: { ticket: TicketDetail }) {
  const api = usePanelApi();
  const input = useRef<HTMLInputElement>(null);
  const [localError, setLocalError] = useState<string | null>(null);
  const [viewing, setViewing] = useState<number | null>(null);
  const upload = useTicketMutation(ticket.id, (file: { type: string; data: string }) =>
    api.attachToTicket(ticket.id, file.type, file.data),
  );
  const closed = ticket.status === 'cerrado';

  const pick = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    setLocalError(null);
    if (!file) return;
    if (!IMAGE_TYPES.includes(file.type)) return setLocalError('Solo se aceptan imágenes JPG, PNG o WebP.');
    if (file.size > MAX_IMAGE_BYTES) return setLocalError('La imagen no puede pasar de 5 MB.');
    const reader = new FileReader();
    reader.onload = () => upload.mutate({ type: file.type, data: String(reader.result) });
    reader.onerror = () => setLocalError('No se pudo leer el archivo.');
    reader.readAsDataURL(file);
  };

  return (
    <Card
      title={`Imágenes (${ticket.attachments.length})`}
      aside={
        !closed && (
          <>
            <input
              ref={input}
              type="file"
              accept={IMAGE_TYPES.join(',')}
              hidden
              onChange={pick}
              aria-label="Elegir imagen"
            />
            <button
              type="button"
              className="btn btn-outline"
              disabled={upload.isPending}
              onClick={() => input.current?.click()}
            >
              {upload.isPending ? 'Subiendo…' : 'Adjuntar imagen'}
            </button>
          </>
        )
      }
    >
      {(localError ?? upload.error?.message) && (
        <p className="auth-error" role="alert">
          {localError ?? upload.error?.message}
        </p>
      )}
      {ticket.attachments.length === 0 ? (
        <p className="muted">Fotos o capturas del problema (JPG, PNG o WebP, hasta 5 MB).</p>
      ) : (
        <ul className="attachment-grid">
          {ticket.attachments.map((a) => (
            <li key={a.id}>
              <button
                type="button"
                className="attachment-thumb"
                onClick={() => setViewing(a.id)}
                aria-label={`Ver imagen ${a.id}`}
              >
                <TicketImage ticketId={ticket.id} fileId={a.id} />
              </button>
            </li>
          ))}
        </ul>
      )}
      {viewing !== null && (
        <Modal title="Imagen adjunta" onClose={() => setViewing(null)} wide>
          <TicketImage ticketId={ticket.id} fileId={viewing} large />
        </Modal>
      )}
    </Card>
  );
}

/** Las imágenes se piden con la sesión (no hay URL pública) y quedan en caché. */
function TicketImage({ ticketId, fileId, large }: { ticketId: number; fileId: number; large?: boolean }) {
  const api = usePanelApi();
  const image = useQuery({
    queryKey: ['adjunto', ticketId, fileId],
    queryFn: () => api.ticketAttachment(ticketId, fileId),
    staleTime: Infinity,
  });
  if (image.isPending) return <span className="attachment-loading" />;
  if (image.isError) return <span className="muted">No disponible</span>;
  return (
    <img
      className={large ? 'attachment-large' : undefined}
      src={`data:${image.data.contentType};base64,${image.data.data}`}
      alt={`Imagen ${fileId} del ticket ${ticketId}`}
    />
  );
}

function EditTicketDialog({ ticket, onClose }: { ticket: TicketDetail; onClose: () => void }) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    titulo: ticket.title,
    descripcion: ticket.description ?? '',
    prioridad: ticket.priority,
  });
  const save = useTicketMutation(ticket.id, () =>
    api.updateTicket(ticket.id, {
      titulo: form.titulo.trim(),
      descripcion: form.descripcion.trim() || null,
      prioridad: form.prioridad,
    }),
  );

  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate(undefined, { onSuccess: onClose });
  };

  return (
    <Modal
      title={`Editar ticket #${ticket.id}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form="edit-ticket" className="btn btn-primary" disabled={save.isPending}>
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
        </>
      }
    >
      <form id="edit-ticket" className="form-grid form-grid-single" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field">
          <span>Título *</span>
          <input
            required
            maxLength={200}
            value={form.titulo}
            onChange={(e) => setForm({ ...form, titulo: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Descripción</span>
          <textarea
            rows={5}
            maxLength={4000}
            value={form.descripcion}
            onChange={(e) => setForm({ ...form, descripcion: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Prioridad</span>
          <select
            value={form.prioridad}
            onChange={(e) => setForm({ ...form, prioridad: e.target.value as TicketPriority })}
          >
            {(Object.keys(PRIORITY_LABEL) as TicketPriority[]).map((p) => (
              <option key={p} value={p}>
                {PRIORITY_LABEL[p]}
              </option>
            ))}
          </select>
        </label>
      </form>
    </Modal>
  );
}

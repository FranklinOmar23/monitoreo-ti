import { STATUS_LABELS, type MachineStatus } from '@monitoreoti/shared';

interface StatusBadgeProps {
  status: MachineStatus;
  /** Texto propio cuando el contexto lo pide (por ejemplo "Falla crítica" en la ficha). */
  label?: string;
}

/** Insignia de estado con el color del semáforo y su texto (el color nunca va solo). */
export function StatusBadge({ status, label }: StatusBadgeProps) {
  return (
    <span className="status-badge" data-status={status}>
      <span className="status-dot" aria-hidden="true" />
      {label ?? STATUS_LABELS[status]}
    </span>
  );
}

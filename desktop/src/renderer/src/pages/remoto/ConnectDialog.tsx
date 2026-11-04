import { useState, type FormEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { MachineDetail } from '@monitoreoti/shared';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { StatusBadge } from '@components/StatusBadge';
import { usePanelApi } from '../../data/PanelData';
import { QUALITIES } from './desktopOptions';

/** Lo elegido antes de conectar (W-28a): viaja a la sesión y a la ventana del escritorio. */
export interface DesktopChoice {
  soloVer: boolean;
  calidad: number;
  motivo: string;
  pedirPermiso: boolean;
  sincronizarPortapapeles: boolean;
}

interface ConnectDialogProps {
  machine: MachineDetail;
  onCancel: () => void;
  onConnect: (choice: DesktopChoice) => void;
}

/**
 * W-28a Antes de conectar: modo (control total o solo ver), calidad, motivo (queda en la auditoría), pedir permiso al
 * usuario (siempre, si la empresa lo exige) y sincronizar el portapapeles.
 */
export function ConnectDialog({ machine, onCancel, onConnect }: ConnectDialogProps) {
  const api = usePanelApi();
  const settings = useQuery({ queryKey: ['remoto', 'configuracion'], queryFn: () => api.remoto.remoteSettings() });
  const companyAsks = settings.data?.pedirPermiso === true;
  const [choice, setChoice] = useState<DesktopChoice>({
    soloVer: false,
    calidad: 0,
    motivo: '',
    pedirPermiso: false,
    sincronizarPortapapeles: true,
  });
  const user = machine.assignedTo ?? 'el usuario del equipo';
  const details = [machine.branchName, machine.departmentName, machine.assignedTo, machine.ipAddress].filter(Boolean);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onConnect({ ...choice, motivo: choice.motivo.trim(), pedirPermiso: companyAsks || choice.pedirPermiso });
  };

  return (
    <Modal
      title="Conectar a escritorio remoto"
      onClose={onCancel}
      footer={
        <>
          <button type="button" className="link-quiet" onClick={onCancel}>
            Cancelar
          </button>
          <button type="submit" form="connect-desktop" className="btn btn-primary">
            <Icon name="remote" size={16} /> Conectar
          </button>
        </>
      }
    >
      <form id="connect-desktop" className="connect-form" onSubmit={submit}>
        <div className="connect-machine">
          <span className="connect-machine-icon" data-status={machine.status} aria-hidden="true">
            <Icon name="monitor" size={20} />
          </span>
          <div>
            <div className="connect-machine-title">
              <strong className="mono">{machine.name}</strong>
              <StatusBadge status={machine.status} />
            </div>
            <span className="muted">{details.join(' · ')}</span>
          </div>
        </div>

        <fieldset className="connect-modes">
          <legend>Modo</legend>
          {(
            [
              [false, 'cursor', 'Control total', 'Usas su teclado y mouse.'],
              [true, 'eye', 'Solo ver', 'Ves la pantalla sin tocar nada.'],
            ] as const
          ).map(([value, icon, title, hint]) => (
            <label key={title} className="connect-mode" data-selected={choice.soloVer === value}>
              <input
                type="radio"
                name="modo"
                checked={choice.soloVer === value}
                onChange={() => setChoice({ ...choice, soloVer: value })}
              />
              <strong>
                <Icon name={icon} size={15} /> {title}
              </strong>
              <span className="muted">{hint}</span>
            </label>
          ))}
        </fieldset>

        <label className="field">
          <span>Calidad</span>
          <select value={choice.calidad} onChange={(e) => setChoice({ ...choice, calidad: Number(e.target.value) })}>
            {QUALITIES.map((q) => (
              <option key={q.value} value={q.value}>
                {q.label}
              </option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>Motivo de la sesión</span>
          <input
            maxLength={300}
            value={choice.motivo}
            placeholder="Por ejemplo: respaldar datos antes de cambiar el disco · TCK-1042"
            onChange={(e) => setChoice({ ...choice, motivo: e.target.value })}
            autoFocus
          />
          <small className="muted">Se guarda en la bitácora de auditoría.</small>
        </label>

        <div className="connect-options">
          <label className="connect-switch">
            <span>
              Pedir permiso a {user} antes de conectar
              {companyAsks && <small className="muted"> (su empresa lo exige)</small>}
            </span>
            <input
              type="checkbox"
              role="switch"
              checked={companyAsks || choice.pedirPermiso}
              disabled={companyAsks}
              onChange={(e) => setChoice({ ...choice, pedirPermiso: e.target.checked })}
            />
          </label>
          <label className="connect-switch">
            <span>Sincronizar portapapeles</span>
            <input
              type="checkbox"
              role="switch"
              checked={choice.sincronizarPortapapeles}
              disabled={choice.soloVer}
              onChange={(e) => setChoice({ ...choice, sincronizarPortapapeles: e.target.checked })}
            />
          </label>
          <p className="muted connect-note">
            <span className="status-dot-live" aria-hidden="true" /> La sesión queda registrada en la bitácora de
            auditoría.
          </p>
        </div>
      </form>
    </Modal>
  );
}

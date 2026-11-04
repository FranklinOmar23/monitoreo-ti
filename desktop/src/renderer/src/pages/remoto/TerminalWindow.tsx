import { useEffect, useRef, useState } from 'react';
import type { TerminalLaunch } from '../../../../preload/bridge';
import { browserSocket, connectRelay, type RelaySocketFactory } from './RemoteSession';
import { loadWindowTicket } from './windowTicket';
import { TerminalView, type TerminalStatus } from './TerminalView';

type Phase =
  | { kind: 'cargando' }
  | { kind: 'esperando' }
  | { kind: 'conectada'; socket: WebSocket }
  | { kind: 'error'; message: string };

interface TerminalWindowProps {
  /** Pide al proceso principal el ticket de esta ventana (una sola vez). */
  loadTicket?: () => Promise<TerminalLaunch | null>;
  createSocket?: RelaySocketFactory;
}

/**
 * Ventana propia de la terminal (sección 12): la ventana principal pidió la sesión (permiso, 2FA) y el proceso
 * principal le entrega a esta el token de un solo uso. Cerrar la ventana cierra la sesión en el relay.
 */
export function TerminalWindow({ loadTicket = loadWindowTicket, createSocket = browserSocket }: TerminalWindowProps) {
  const [phase, setPhase] = useState<Phase>({ kind: 'cargando' });
  const [launch, setLaunch] = useState<TerminalLaunch | null>(null);
  const [status, setStatus] = useState<TerminalStatus | null>(null);
  const socketRef = useRef<WebSocket | null>(null);
  const loader = useRef(loadTicket);

  useEffect(() => {
    let cancelled = false;
    void loader.current().then((ticket) => {
      if (cancelled) return;
      if (!ticket) {
        setPhase({
          kind: 'error',
          message: 'Esta ventana ya no tiene una sesión. Ábrala de nuevo desde la ficha del equipo.',
        });
        return;
      }
      setLaunch(ticket);
      document.title = `Terminal · ${ticket.machineName}`;
      setPhase({ kind: 'esperando' });
      socketRef.current = connectRelay(ticket.serverUrl, ticket, createSocket, {
        onConnected: (socket) => setPhase({ kind: 'conectada', socket }),
        onFail: (message) => setPhase({ kind: 'error', message }),
        onClosed: (connected) =>
          setPhase((p) =>
            p.kind === 'error' || connected ? p : { kind: 'error', message: 'El equipo no respondió a tiempo.' },
          ),
      });
    });
    return () => {
      cancelled = true;
      socketRef.current?.close();
    };
  }, [createSocket]);

  const disconnect = () => {
    socketRef.current?.close();
    window.close();
  };

  return (
    <div className="terminal-window">
      <header className="terminal-bar">
        <strong>{launch ? `Terminal · ${launch.machineName}` : 'Terminal'}</strong>
        <span className="muted" role="status">
          {phase.kind === 'cargando' && 'Preparando…'}
          {phase.kind === 'esperando' && 'Esperando a que el equipo se conecte…'}
          {phase.kind === 'conectada' &&
            (status?.kind === 'lista' ? `${status.shell === 'cmd' ? 'CMD' : 'PowerShell'} · como SYSTEM` : 'Conectada')}
          {phase.kind === 'error' && 'Sin conexión'}
        </span>
        <button type="button" className="btn btn-outline" onClick={disconnect}>
          Desconectar
        </button>
      </header>
      {phase.kind === 'conectada' ? (
        <TerminalView socket={phase.socket} onStatus={setStatus} />
      ) : (
        <div className="session-status terminal-status" data-phase={phase.kind === 'error' ? 'error' : 'esperando'}>
          {phase.kind !== 'error' && <span className="spinner" aria-hidden="true" />}
          <strong>{phase.kind === 'error' ? 'No se pudo conectar.' : 'Conectando…'}</strong>
          {phase.kind === 'error' && <p>{phase.message}</p>}
        </div>
      )}
    </div>
  );
}

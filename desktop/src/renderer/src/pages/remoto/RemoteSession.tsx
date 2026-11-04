import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import {
  ApiError,
  SESSION_TYPE_LABELS,
  type SessionOptions,
  type SessionTicket,
  type SessionType,
} from '@monitoreoti/shared';
import { Modal } from '@components/Modal';
import { useSession } from '../../auth/SessionProvider';
import { usePanelApi } from '../../data/PanelData';
import { needsTwoFactor, TwoFactorPrompt } from './TwoFactorPrompt';

export type SessionPhase = 'solicitando' | 'codigo' | 'esperando' | 'conectado' | 'cerrada' | 'error' | 'entregada';

export interface RemoteSessionState {
  phase: SessionPhase;
  /** Motivo del error o del cierre, en palabras del técnico. */
  message: string | null;
  ticket: SessionTicket | null;
  /** Socket del relay ya conectado con el equipo (solo en "conectado"). */
  socket: WebSocket | null;
  retry: () => void;
  close: () => void;
  confirmCode: () => void;
}

export type RelaySocketFactory = (url: string) => WebSocket;

export const browserSocket: RelaySocketFactory = (url) => {
  const socket = new WebSocket(url);
  socket.binaryType = 'arraybuffer';
  return socket;
};

/** URL del relay en el mismo servidor del panel (ws o wss según http o https). */
export function relayUrl(serverUrl: string, relayPath: string): string {
  const url = new URL(relayPath, serverUrl);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  return url.href;
}

export interface RelayCallbacks {
  /** El relay confirmó que el equipo llegó: desde aquí el visor usa el socket. */
  onConnected: (socket: WebSocket) => void;
  onFail: (message: string) => void;
  /** El socket se cerró (connected: si llegó a conectarse con el equipo). */
  onClosed: (connected: boolean) => void;
}

/**
 * Se conecta al relay con el token de un solo uso (primer mensaje) y espera al equipo. Los mensajes {"relay": ...}
 * son del relay; un {"tipo":"error"} es el agente explicando por qué no puede atender la sesión.
 */
export function connectRelay(
  serverUrl: string,
  ticket: Pick<SessionTicket, 'relayPath' | 'token'>,
  createSocket: RelaySocketFactory,
  callbacks: RelayCallbacks,
): WebSocket {
  const ws = createSocket(relayUrl(serverUrl, ticket.relayPath));
  let connected = false;
  ws.onopen = () => ws.send(JSON.stringify({ token: ticket.token }));
  // Hasta que el relay confirme, cada mensaje se lee aquí; luego, el visor (terminal, archivos...) toma el socket.
  ws.onmessage = (event: MessageEvent) => {
    if (connected || typeof event.data !== 'string') return;
    const data = parse(event.data);
    if (data?.['relay'] === 'conectado') {
      connected = true;
      callbacks.onConnected(ws);
    } else if (data?.['tipo'] === 'error') {
      callbacks.onFail(String(data['mensaje'] ?? 'El equipo no pudo abrir la sesión.'));
    } else if (data?.['relay'] === 'cerrada') {
      callbacks.onFail(`La sesión terminó: ${String(data['motivo'] ?? 'sin motivo')}.`);
    }
  };
  ws.onclose = () => callbacks.onClosed(connected);
  return ws;
}

/**
 * Abre una sesión remota (sección 10.1): la pide al servidor (con el 2FA si hace falta) y se conecta al relay. Con
 * handoff, el ticket se entrega a otra ventana (la terminal) y aquí no se conecta: la fase queda en "entregada".
 */
export function useRemoteSession(
  machineId: number,
  type: SessionType,
  createSocket: RelaySocketFactory = browserSocket,
  handoff?: (ticket: SessionTicket) => boolean,
  options?: SessionOptions,
): RemoteSessionState {
  const api = usePanelApi();
  const { serverUrl } = useSession();
  const [phase, setPhase] = useState<SessionPhase>('solicitando');
  const [message, setMessage] = useState<string | null>(null);
  const [ticket, setTicket] = useState<SessionTicket | null>(null);
  const [socket, setSocket] = useState<WebSocket | null>(null);
  const [attempt, setAttempt] = useState(0);
  const current = useRef<{ socket: WebSocket | null; ticket: SessionTicket | null }>({ socket: null, ticket: null });

  const fail = useCallback((text: string) => {
    setPhase('error');
    setMessage(text);
    setSocket(null);
  }, []);

  useEffect(() => {
    let cancelled = false;

    api.remoto
      .openSession(machineId, type, options)
      .then((opened) => {
        // La pantalla ya se cerró (o React repitió el efecto): esa sesión no se usará.
        if (cancelled) {
          void api.remoto.closeSession(opened.sessionId).catch(() => undefined);
          return;
        }
        setTicket(opened);
        // La otra ventana se encarga desde aquí (y de cerrar la sesión).
        if (handoff?.(opened)) {
          setPhase('entregada');
          return;
        }
        setPhase('esperando');
        const ws = connectRelay(serverUrl, opened, createSocket, {
          onConnected: (connectedSocket) => {
            setPhase('conectado');
            setSocket(connectedSocket);
          },
          onFail: fail,
          onClosed: (connected) => {
            if (cancelled) return;
            setSocket(null);
            setPhase((p) => (p === 'error' ? p : connected ? 'cerrada' : 'error'));
            setMessage((m) => m ?? (connected ? 'La sesión terminó.' : 'El equipo no respondió a tiempo.'));
          },
        });
        current.current = { socket: ws, ticket: opened };
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (needsTwoFactor(error)) setPhase('codigo');
        else fail(error instanceof ApiError ? error.message : 'No se pudo abrir la sesión.');
      });

    return () => {
      cancelled = true;
      const { socket: ws, ticket: t } = current.current;
      current.current = { socket: null, ticket: null };
      ws?.close();
      // Al salir de la pantalla la sesión se cierra también en el servidor (si el socket no alcanzó a avisar).
      if (t) void api.remoto.closeSession(t.sessionId).catch(() => undefined);
    };
  }, [api, machineId, type, serverUrl, createSocket, handoff, options, attempt, fail]);

  return {
    phase,
    message,
    ticket,
    socket,
    retry: () => {
      setPhase('solicitando');
      setMessage(null);
      setSocket(null);
      setAttempt((n) => n + 1);
    },
    close: () => current.current.socket?.close(),
    confirmCode: () => {
      setPhase('solicitando');
      setAttempt((n) => n + 1);
    },
  };
}

function parse(text: string): Record<string, unknown> | null {
  try {
    const value: unknown = JSON.parse(text);
    return value && typeof value === 'object' ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

const PHASE_TEXT: Record<SessionPhase, string> = {
  solicitando: 'Pidiendo la sesión al servidor…',
  codigo: 'Confirme su código de verificación para continuar.',
  esperando: 'Esperando a que el equipo se conecte…',
  conectado: 'Conectado.',
  cerrada: 'La sesión terminó.',
  error: 'No se pudo conectar.',
  entregada: 'La sesión se abrió en otra ventana.',
};

interface RemoteSessionDialogProps {
  machineId: number;
  machineName: string;
  type: SessionType;
  onClose: () => void;
  createSocket?: RelaySocketFactory;
  /** El visor de la sesión ya conectada (terminal, archivos, escritorio). */
  children?: (socket: WebSocket) => ReactNode;
  /** Entregar el ticket a otra ventana (la terminal); true si se entregó. */
  handoff?: (ticket: SessionTicket) => boolean;
  /** Opciones elegidas antes de conectar (W-28a). */
  options?: SessionOptions;
}

/** W-29 Conectando / error, y el visor de la sesión cuando conecta. */
export function RemoteSessionDialog({
  machineId,
  machineName,
  type,
  onClose,
  createSocket,
  children,
  handoff,
  options,
}: RemoteSessionDialogProps) {
  const session = useRemoteSession(machineId, type, createSocket, handoff, options);
  // Entregada a su propia ventana: este diálogo ya no hace falta.
  useEffect(() => {
    if (session.phase === 'entregada') onClose();
  }, [session.phase, onClose]);
  const busy = session.phase === 'solicitando' || session.phase === 'esperando';

  return (
    <>
      <Modal title={`${SESSION_TYPE_LABELS[type]} · ${machineName}`} onClose={onClose} wide>
        {session.phase === 'conectado' && session.socket && children ? (
          children(session.socket)
        ) : (
          <div className="session-status" data-phase={session.phase} role="status" aria-live="polite">
            {busy && <span className="spinner" aria-hidden="true" />}
            <strong>{PHASE_TEXT[session.phase]}</strong>
            {session.message && <p>{session.message}</p>}
            {session.phase === 'esperando' && (
              <p className="muted">
                El equipo tiene 60 segundos para responder. En él se mostrará un aviso de sesión activa.
              </p>
            )}
            {session.phase === 'conectado' && !children && (
              <p className="muted">
                El equipo está conectado, pero este tipo de sesión aún no tiene visor en el panel.
              </p>
            )}
            {(session.phase === 'error' || session.phase === 'cerrada') && (
              <div className="session-actions">
                <button type="button" className="btn btn-outline" onClick={onClose}>
                  Cerrar
                </button>
                <button type="button" className="btn btn-primary" onClick={session.retry}>
                  Reintentar
                </button>
              </div>
            )}
          </div>
        )}
      </Modal>
      {session.phase === 'codigo' && <TwoFactorPrompt onCancel={onClose} onConfirmed={session.confirmCode} />}
    </>
  );
}

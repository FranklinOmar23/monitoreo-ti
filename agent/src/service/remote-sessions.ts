import type { SocketLike } from './gateway-client';

/** Canal de una sesión remota: lo que el agente manda y recibe por el relay (marcos de texto o binarios). */
export interface SessionChannel {
  /** Id de la sesión en el servidor (para informar lo que se hizo en ella). */
  readonly sessionId: number;
  /** Quién se conecta (para el aviso en el equipo) y si el usuario debe aceptar antes. */
  readonly technician: string;
  readonly askConsent: boolean;
  /** Escritorio sin control (solo ver). */
  readonly viewOnly: boolean;
  /** El técnico puede transferir archivos dentro del escritorio (remoto.archivos). */
  readonly allowFiles: boolean;
  sendText(text: string): void;
  sendBinary(data: Uint8Array): void;
  close(): void;
}

/** Lo que atiende una sesión de un tipo (terminal, archivos, escritorio); se libera al cerrar. */
export interface SessionHandler {
  onText(text: string): void;
  onBinary(data: Uint8Array): void;
  dispose(): void;
}

export type SessionHandlerFactory = (channel: SessionChannel) => SessionHandler;

export interface RemoteSessionsOptions {
  serverUrl: URL;
  /** Un tipo sin fábrica se rechaza con un mensaje al técnico. */
  handlers: Partial<Record<string, SessionHandlerFactory>>;
  createSocket: (url: string) => SocketLike & { binaryType?: string };
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
}

interface OpenRequest {
  sesion_id: number;
  tipo: string;
  url_relay: string;
  token_un_uso: string;
  tecnico?: string;
  pedir_permiso?: boolean;
  solo_ver?: boolean;
  archivos?: boolean;
}

interface ActiveSession {
  socket: SocketLike;
  handler: SessionHandler | null;
}

/** Node 22+ trae WebSocket global. */
export const nodeRelaySocket: RemoteSessionsOptions['createSocket'] = (url) => {
  const Ctor = (globalThis as unknown as { WebSocket: new (url: string) => SocketLike & { binaryType: string } })
    .WebSocket;
  const socket = new Ctor(url);
  socket.binaryType = 'arraybuffer';
  return socket;
};

/**
 * Sesiones remotas del agente (sección 10.1): ante "abrir_sesion_remota" se conecta al relay del mismo servidor con
 * su token de un solo uso y pasa los marcos al manejador del tipo; "cerrar_sesion_remota" o el cierre del relay la
 * terminan. El relay solo acepta el token una vez y durante 60 s.
 */
export class RemoteSessions {
  private readonly sessions = new Map<number, ActiveSession>();

  constructor(private readonly options: RemoteSessionsOptions) {}

  get activeCount(): number {
    return this.sessions.size;
  }

  open(datos: unknown): void {
    const request = parseOpen(datos);
    if (!request) {
      this.options.log('error', 'Pedido de sesión remota con formato inválido; se descarta');
      return;
    }

    const url = this.relayUrl(request.url_relay);
    if (!url) {
      this.options.log('error', 'Relay fuera del servidor del agente; se rechaza', { sesionId: request.sesion_id });
      return;
    }

    this.close(request.sesion_id);
    const socket = this.options.createSocket(url);
    const session: ActiveSession = { socket, handler: null };
    this.sessions.set(request.sesion_id, session);

    const channel: SessionChannel = {
      sessionId: request.sesion_id,
      technician: typeof request.tecnico === 'string' && request.tecnico ? request.tecnico : 'Un técnico',
      askConsent: request.pedir_permiso === true,
      viewOnly: request.solo_ver === true,
      allowFiles: request.archivos === true,
      sendText: (text) => send(socket, text),
      sendBinary: (data) => send(socket, data),
      close: () => this.close(request.sesion_id),
    };

    socket.onopen = () => {
      socket.send(JSON.stringify({ token: request.token_un_uso }));
      const factory = this.options.handlers[request.tipo];
      if (!factory) {
        channel.sendText(
          JSON.stringify({ tipo: 'error', mensaje: `Este agente todavía no admite sesiones de tipo ${request.tipo}.` }),
        );
        this.close(request.sesion_id);
        return;
      }
      try {
        session.handler = factory(channel);
        this.options.log('info', 'Sesión remota abierta', { sesionId: request.sesion_id, tipo: request.tipo });
      } catch (error) {
        channel.sendText(JSON.stringify({ tipo: 'error', mensaje: `No se pudo iniciar la sesión: ${String(error)}` }));
        this.close(request.sesion_id);
      }
    };
    socket.onmessage = (event) => {
      const data = event.data;
      if (typeof data === 'string') session.handler?.onText(data);
      else if (data instanceof ArrayBuffer) session.handler?.onBinary(new Uint8Array(data));
      else if (ArrayBuffer.isView(data))
        session.handler?.onBinary(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
    };
    socket.onclose = () => this.release(request.sesion_id, socket);
    socket.onerror = () => {
      // El motivo llega en onclose.
    };
  }

  /** "cerrar_sesion_remota" o cierre local. */
  close(sessionId: number): void {
    const session = this.sessions.get(sessionId);
    if (!session) return;
    this.release(sessionId, session.socket);
    try {
      session.socket.close(1000, 'Sesión terminada');
    } catch {
      // Ya cerrado.
    }
  }

  closeAll(): void {
    for (const id of [...this.sessions.keys()]) this.close(id);
  }

  private release(sessionId: number, socket: SocketLike): void {
    const session = this.sessions.get(sessionId);
    if (!session || session.socket !== socket) return;
    this.sessions.delete(sessionId);
    try {
      session.handler?.dispose();
    } catch (error) {
      this.options.log('error', 'Falló al liberar la sesión remota', { sesionId: sessionId, error: String(error) });
    }
    this.options.log('info', 'Sesión remota cerrada', { sesionId: sessionId });
  }

  /** Solo rutas /relay/{id} del mismo servidor al que está conectado el agente. */
  private relayUrl(path: string): string | null {
    if (!/^\/relay\/\d+$/.test(path)) return null;
    const url = new URL(path, this.options.serverUrl);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
    return url.href;
  }
}

function send(socket: SocketLike, data: string | Uint8Array): void {
  try {
    (socket as unknown as { send(data: string | Uint8Array): void }).send(data);
  } catch {
    // Se cerró: el cierre libera la sesión.
  }
}

function parseOpen(value: unknown): OpenRequest | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  return Number.isInteger(v['sesion_id']) &&
    typeof v['tipo'] === 'string' &&
    typeof v['url_relay'] === 'string' &&
    typeof v['token_un_uso'] === 'string'
    ? (v as unknown as OpenRequest)
    : null;
}

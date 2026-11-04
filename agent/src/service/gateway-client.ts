import { createEnvelope, isEnvelope, type Envelope } from '@monitoreoti/shared';
import { parseCollectorConfig, type CollectorConfig } from './snmp';

/** Lo mínimo del WebSocket que usa el cliente; Node (undici) y las pruebas lo cumplen. */
export interface SocketLike {
  send(data: string): void;
  close(code?: number, reason?: string): void;
  onopen: ((event: unknown) => void) | null;
  onmessage: ((event: { data: unknown }) => void) | null;
  onclose: ((event: { code: number; reason: string }) => void) | null;
  onerror: ((event: unknown) => void) | null;
}

export type SocketFactory = (url: string, headers: Record<string, string>) => SocketLike;

/** Node 22+ trae WebSocket global y, a diferencia del estándar del navegador, permite cabeceras. */
const nodeSocketFactory: SocketFactory = (url, headers) => {
  const Ctor = (
    globalThis as unknown as {
      WebSocket: new (url: string, options: { headers: Record<string, string> }) => SocketLike;
    }
  ).WebSocket;
  return new Ctor(url, { headers });
};

export interface ServerWelcome {
  heartbeatSeconds: number;
  metricsSeconds: number;
  isCollector: boolean;
  /** Dispositivos a consultar por SNMP; null si este agente no es recolector. */
  collector: CollectorConfig | null;
  /** Llave pública de firma de la empresa (para los agentes registrados antes de que existiera). */
  companyPublicKey?: string | null;
}

export interface GatewayOptions {
  serverUrl: URL;
  credential: string;
  agentVersion: string;
  hostname: string;
  operatingSystem: string;
  /** Segundos de heartbeat hasta que el servidor indique otro valor en la bienvenida. */
  heartbeatSeconds?: number;
  onReady?: (welcome: ServerWelcome) => void;
  onDisconnected?: () => void;
  /** Mensajes del servidor distintos de ack y bienvenida (comandos, configuración...). */
  onMessage?: (message: Envelope) => void;
  log?: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  createSocket?: SocketFactory;
  random?: () => number;
}

const HELLO_TIMEOUT_MS = 10_000;
const BACKOFF_BASE_MS = 1_000;
const BACKOFF_MAX_MS = 60_000;

/** Espera exponencial con tope y ±20% de variación, para que una flota no se reconecte toda a la vez. */
export function backoffDelay(attempt: number, random: () => number = Math.random): number {
  const exponential = Math.min(BACKOFF_BASE_MS * 2 ** attempt, BACKOFF_MAX_MS);
  return Math.round(exponential * (0.8 + random() * 0.4));
}

interface Pending {
  resolve: () => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * Conexión permanente del agente al gateway (/agent/ws). Siempre la abre el agente (WSS saliente por 443):
 * funciona detrás del router de cualquier sucursal. Si se cae, reconecta sola.
 */
export class GatewayClient {
  private socket: SocketLike | undefined;
  private stopped = true;
  private ready = false;
  private attempt = 0;
  private heartbeatTimer: ReturnType<typeof setInterval> | undefined;
  private helloTimer: ReturnType<typeof setTimeout> | undefined;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private lastReceivedAt = 0;
  private heartbeatSeconds: number;
  private readonly pending = new Map<string, Pending>();

  constructor(private readonly options: GatewayOptions) {
    this.heartbeatSeconds = options.heartbeatSeconds ?? 30;
  }

  get isReady(): boolean {
    return this.ready;
  }

  start(): void {
    if (!this.stopped) return;
    this.stopped = false;
    this.connect();
  }

  stop(): void {
    this.stopped = true;
    clearTimeout(this.reconnectTimer);
    this.teardown(new Error('El cliente se detuvo.'));
    this.socket?.close(1000, 'Servicio detenido');
    this.socket = undefined;
  }

  /**
   * Envía un mensaje y espera la confirmación (ack) del servidor. Rechaza si no hay conexión lista,
   * si se cae antes del ack o si vence el tiempo: el llamador conserva el mensaje en su cola y reintenta.
   */
  request(envelope: Envelope, timeoutMs = 15_000): Promise<void> {
    const socket = this.socket;
    if (!this.ready || !socket) return Promise.reject(new Error('Sin conexión con el servidor.'));

    return new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(envelope.id);
        reject(new Error(`El servidor no confirmó el mensaje ${envelope.tipo}.`));
      }, timeoutMs);
      this.pending.set(envelope.id, { resolve, reject, timer });

      try {
        socket.send(JSON.stringify(envelope));
      } catch (error) {
        clearTimeout(timer);
        this.pending.delete(envelope.id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  private connect(): void {
    if (this.stopped) return;

    const url = new URL('/agent/ws', this.options.serverUrl);
    url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';

    let socket: SocketLike;
    try {
      socket = (this.options.createSocket ?? nodeSocketFactory)(url.href, {
        Authorization: `Bearer ${this.options.credential}`,
      });
    } catch (error) {
      this.log('error', 'No se pudo abrir la conexión', { error: String(error) });
      this.scheduleReconnect();
      return;
    }

    this.socket = socket;
    socket.onopen = () => this.onOpen(socket);
    socket.onmessage = (event) => this.onSocketMessage(socket, event.data);
    socket.onclose = (event) =>
      this.onClosed(socket, `código ${event.code}${event.reason ? ` (${event.reason})` : ''}`);
    socket.onerror = () => {
      // Antes del cierre solo informa que falló; el motivo real llega en onclose.
    };
  }

  private onOpen(socket: SocketLike): void {
    this.send(
      socket,
      createEnvelope('hola', {
        version: this.options.agentVersion,
        hostname: this.options.hostname,
        so: this.options.operatingSystem,
        es_recolector: false,
      }),
    );
    this.lastReceivedAt = Date.now();
    // Sin bienvenida a tiempo la conexión no sirve: se cierra y se reintenta.
    this.helloTimer = setTimeout(() => socket.close(4000, 'Sin bienvenida'), HELLO_TIMEOUT_MS);
  }

  private onSocketMessage(socket: SocketLike, data: unknown): void {
    if (socket !== this.socket || typeof data !== 'string') return;
    this.lastReceivedAt = Date.now();

    let message: unknown;
    try {
      message = JSON.parse(data);
    } catch {
      return;
    }
    if (!isEnvelope(message)) return;

    switch (message.tipo) {
      case 'ack': {
        const id = (message.datos as { id?: unknown } | null)?.id;
        const entry = typeof id === 'string' ? this.pending.get(id) : undefined;
        if (entry) {
          clearTimeout(entry.timer);
          this.pending.delete(id as string);
          entry.resolve();
        }
        break;
      }
      case 'bienvenida':
        this.onWelcome(message.datos);
        break;
      default:
        this.options.onMessage?.(message);
    }
  }

  private onWelcome(datos: unknown): void {
    const d = (datos ?? {}) as Record<string, unknown>;
    const welcome: ServerWelcome = {
      heartbeatSeconds: positiveInteger(d['heartbeat_s']) ?? this.heartbeatSeconds,
      metricsSeconds: positiveInteger(d['metricas_s']) ?? 60,
      isCollector: d['es_recolector'] === true,
      collector: parseCollectorConfig(d['recolector']),
      companyPublicKey: typeof d['llave_empresa'] === 'string' && d['llave_empresa'] ? d['llave_empresa'] : null,
    };

    clearTimeout(this.helloTimer);
    this.heartbeatSeconds = welcome.heartbeatSeconds;
    this.ready = true;
    this.attempt = 0;
    this.log('info', 'Conectado al servidor', { heartbeatSeconds: welcome.heartbeatSeconds });

    this.heartbeatTimer = setInterval(() => this.heartbeat(), this.heartbeatSeconds * 1000);
    this.options.onReady?.(welcome);
  }

  private heartbeat(): void {
    const socket = this.socket;
    if (!socket || !this.ready) return;

    // Tres heartbeats sin oír nada del servidor (ni siquiera acks): la conexión está muerta aunque el socket no lo sepa.
    if (Date.now() - this.lastReceivedAt > this.heartbeatSeconds * 3 * 1000) {
      this.log('error', 'El servidor dejó de responder; se reinicia la conexión');
      socket.close(4001, 'Sin respuesta');
      this.onClosed(socket, 'sin respuesta del servidor');
      return;
    }

    this.send(socket, createEnvelope('heartbeat', { uptimeSeconds: Math.round(process.uptime()) }));
  }

  private onClosed(socket: SocketLike, reason: string): void {
    if (socket !== this.socket) return;
    this.socket = undefined;
    const wasReady = this.ready;
    this.teardown(new Error('Se perdió la conexión con el servidor.'));

    if (wasReady) this.options.onDisconnected?.();
    this.log('info', 'Conexión cerrada', { reason });
    this.scheduleReconnect();
  }

  /** Limpia temporizadores y rechaza los mensajes que esperaban ack. */
  private teardown(error: Error): void {
    clearInterval(this.heartbeatTimer);
    clearTimeout(this.helloTimer);
    this.ready = false;
    for (const entry of this.pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(error);
    }
    this.pending.clear();
  }

  private scheduleReconnect(): void {
    if (this.stopped) return;
    const delay = backoffDelay(this.attempt++, this.options.random);
    this.log('info', 'Reintentando conexión', { inMs: delay });
    this.reconnectTimer = setTimeout(() => this.connect(), delay);
  }

  private send(socket: SocketLike, envelope: Envelope): void {
    try {
      socket.send(JSON.stringify(envelope));
    } catch (error) {
      this.log('error', 'No se pudo enviar', { tipo: envelope.tipo, error: String(error) });
    }
  }

  private log(level: 'info' | 'error', message: string, extra?: Record<string, unknown>): void {
    this.options.log?.(level, message, extra);
  }
}

function positiveInteger(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 ? value : undefined;
}

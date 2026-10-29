import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import { backoffDelay, GatewayClient, type ServerWelcome, type SocketLike } from './gateway-client';

class FakeSocket implements SocketLike {
  onopen: SocketLike['onopen'] = null;
  onmessage: SocketLike['onmessage'] = null;
  onclose: SocketLike['onclose'] = null;
  onerror: SocketLike['onerror'] = null;
  sent: { tipo: string; id: string; datos: Record<string, unknown> }[] = [];
  closed: { code?: number; reason?: string } | undefined;

  constructor(
    readonly url: string,
    readonly headers: Record<string, string>,
  ) {}

  send(data: string): void {
    this.sent.push(JSON.parse(data));
  }

  close(code?: number, reason?: string): void {
    this.closed = { code, reason };
  }

  open(): void {
    this.onopen?.({});
  }

  receive(tipo: string, datos: unknown, id = 'srv-1'): void {
    this.onmessage?.({ data: JSON.stringify({ v: 1, tipo, id, ts: new Date().toISOString(), datos }) });
  }

  drop(code = 1006): void {
    this.onclose?.({ code, reason: '' });
  }
}

describe('backoffDelay', () => {
  it('crece de forma exponencial hasta el tope de un minuto', () => {
    const middle = () => 0.5; // sin variación: factor 1.0
    expect([0, 1, 2, 3].map((n) => backoffDelay(n, middle))).toEqual([1000, 2000, 4000, 8000]);
    expect(backoffDelay(20, middle)).toBe(60_000);
  });

  it('varía ±20% para que una flota no se reconecte toda a la vez', () => {
    expect(backoffDelay(2, () => 0)).toBe(3200);
    expect(backoffDelay(2, () => 1)).toBe(4800);
  });
});

describe('GatewayClient', () => {
  let sockets: FakeSocket[];
  let ready: Mock<(welcome: ServerWelcome) => void>;
  let disconnected: Mock<() => void>;

  const createClient = () =>
    new GatewayClient({
      serverUrl: new URL('https://api.monitoreoti.do'),
      credential: '1.secreto',
      agentVersion: '0.1.0',
      hostname: 'CONTA-03',
      operatingSystem: 'Windows 11',
      random: () => 0.5,
      onReady: ready,
      onDisconnected: disconnected,
      createSocket: (url, headers) => {
        const socket = new FakeSocket(url, headers);
        sockets.push(socket);
        return socket;
      },
    });

  /** Abre la conexión y completa el saludo. */
  const connect = (client: GatewayClient, heartbeatSeconds = 30) => {
    client.start();
    const socket = sockets.at(-1)!;
    socket.open();
    socket.receive('bienvenida', { heartbeat_s: heartbeatSeconds, metricas_s: 60, es_recolector: false });
    return socket;
  };

  beforeEach(() => {
    vi.useFakeTimers();
    sockets = [];
    ready = vi.fn();
    disconnected = vi.fn();
  });
  afterEach(() => vi.useRealTimers());

  it('abre wss con la credencial en la cabecera y saluda con hola', () => {
    const client = createClient();
    client.start();
    sockets[0]!.open();

    expect(sockets[0]!.url).toBe('wss://api.monitoreoti.do/agent/ws');
    expect(sockets[0]!.headers).toEqual({ Authorization: 'Bearer 1.secreto' });
    expect(sockets[0]!.sent[0]).toMatchObject({
      tipo: 'hola',
      datos: { version: '0.1.0', hostname: 'CONTA-03', so: 'Windows 11', es_recolector: false },
    });
    expect(client.isReady).toBe(false);
  });

  it('queda lista al recibir la bienvenida y envía heartbeat con el intervalo del servidor', () => {
    const client = createClient();
    const socket = connect(client, 10);

    expect(client.isReady).toBe(true);
    expect(ready).toHaveBeenCalledWith({ heartbeatSeconds: 10, metricsSeconds: 60, isCollector: false });

    vi.advanceTimersByTime(10_000);
    vi.advanceTimersByTime(10_000);
    expect(socket.sent.filter((m) => m.tipo === 'heartbeat')).toHaveLength(2);
  });

  it('reconecta con espera exponencial y la reinicia al conectar', () => {
    const client = createClient();
    client.start();
    sockets[0]!.drop(); // falla antes de la bienvenida
    expect(sockets).toHaveLength(1);

    vi.advanceTimersByTime(1000);
    expect(sockets).toHaveLength(2);
    sockets[1]!.drop();
    vi.advanceTimersByTime(1999);
    expect(sockets).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(sockets).toHaveLength(3);

    sockets[2]!.open();
    sockets[2]!.receive('bienvenida', { heartbeat_s: 30 });
    sockets[2]!.drop();
    expect(disconnected).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(1000); // vuelve al primer escalón
    expect(sockets).toHaveLength(4);
  });

  it('cierra y reintenta si no llega la bienvenida', () => {
    const client = createClient();
    client.start();
    sockets[0]!.open();

    vi.advanceTimersByTime(10_000);

    expect(sockets[0]!.closed?.code).toBe(4000);
  });

  it('request resuelve con el ack y rechaza si la conexión se cae antes', async () => {
    const client = createClient();
    const socket = connect(client);

    const ok = client.request({ v: 1, tipo: 'metricas', id: 'm-1', ts: 'x', datos: {} });
    socket.receive('ack', { id: 'm-1' });
    await expect(ok).resolves.toBeUndefined();

    const lost = client.request({ v: 1, tipo: 'metricas', id: 'm-2', ts: 'x', datos: {} });
    const assertion = expect(lost).rejects.toThrow(/Se perdió la conexión/);
    socket.drop();
    await assertion;
  });

  it('request rechaza si no hay conexión lista y si el ack no llega a tiempo', async () => {
    const client = createClient();
    await expect(client.request({ v: 1, tipo: 'x', id: '1', ts: 'x', datos: {} })).rejects.toThrow(/Sin conexión/);

    connect(client);
    const silent = client.request({ v: 1, tipo: 'metricas', id: 'm-9', ts: 'x', datos: {} }, 5_000);
    const assertion = expect(silent).rejects.toThrow(/no confirmó/);
    vi.advanceTimersByTime(5_000);
    await assertion;
  });

  it('reinicia la conexión si el servidor calla durante tres heartbeats', () => {
    const client = createClient();
    const socket = connect(client, 10);

    vi.advanceTimersByTime(40_000);

    expect(socket.closed?.code).toBe(4001);
    expect(disconnected).toHaveBeenCalled();
  });

  it('entrega al llamador los mensajes que no son ack ni bienvenida', () => {
    const onMessage = vi.fn();
    const client = new GatewayClient({
      serverUrl: new URL('http://localhost:5126'),
      credential: 'c',
      agentVersion: '0.1.0',
      hostname: 'H',
      operatingSystem: 'W',
      onMessage,
      createSocket: (url, headers) => {
        const socket = new FakeSocket(url, headers);
        sockets.push(socket);
        return socket;
      },
    });
    client.start();
    sockets[0]!.open();

    sockets[0]!.receive('config_actualizada', { x: 1 });

    expect(sockets[0]!.url).toBe('ws://localhost:5126/agent/ws');
    expect(onMessage).toHaveBeenCalledWith(expect.objectContaining({ tipo: 'config_actualizada' }));
  });

  it('stop cierra el socket y no reconecta', () => {
    const client = createClient();
    const socket = connect(client);

    client.stop();
    socket.drop(1000);
    vi.advanceTimersByTime(120_000);

    expect(socket.closed?.code).toBe(1000);
    expect(sockets).toHaveLength(1);
  });
});

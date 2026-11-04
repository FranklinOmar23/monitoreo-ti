import { describe, expect, it, vi } from 'vitest';
import type { SocketLike } from './gateway-client';
import { RemoteSessions, type SessionHandler } from './remote-sessions';

class FakeSocket implements SocketLike {
  sent: (string | Uint8Array)[] = [];
  closed = false;
  onopen: ((event: unknown) => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: ((event: { code: number; reason: string }) => void) | null = null;
  onerror: ((event: unknown) => void) | null = null;
  constructor(readonly url: string) {}
  send(data: string): void {
    this.sent.push(data);
  }
  close(): void {
    this.closed = true;
    this.onclose?.({ code: 1000, reason: '' });
  }
}

function setup(handlers: ConstructorParameters<typeof RemoteSessions>[0]['handlers'] = {}) {
  const sockets: FakeSocket[] = [];
  const sessions = new RemoteSessions({
    serverUrl: new URL('https://monitoreo.ejemplo.do'),
    handlers,
    createSocket: (url) => {
      const socket = new FakeSocket(url);
      sockets.push(socket);
      return socket;
    },
    log: vi.fn(),
  });
  return { sessions, sockets };
}

const open = { sesion_id: 12, tipo: 'terminal', url_relay: '/relay/12', token_un_uso: 'tok-agente' };

describe('sesiones remotas del agente', () => {
  it('se conecta al relay de su servidor, manda su token y pasa los marcos al manejador', () => {
    const handler: SessionHandler = { onText: vi.fn(), onBinary: vi.fn(), dispose: vi.fn() };
    const { sessions, sockets } = setup({ terminal: () => handler });

    sessions.open(open);
    const socket = sockets[0]!;
    expect(socket.url).toBe('wss://monitoreo.ejemplo.do/relay/12');
    socket.onopen?.({});
    expect(socket.sent[0]).toBe(JSON.stringify({ token: 'tok-agente' }));

    socket.onmessage?.({ data: 'dir\r' });
    socket.onmessage?.({ data: new Uint8Array([1, 2]).buffer });
    expect(handler.onText).toHaveBeenCalledWith('dir\r');
    expect(handler.onBinary).toHaveBeenCalledWith(new Uint8Array([1, 2]));

    sessions.close(12);
    expect(handler.dispose).toHaveBeenCalled();
    expect(socket.closed).toBe(true);
    expect(sessions.activeCount).toBe(0);
  });

  it('le pasa al manejador quién se conecta y si debe pedir permiso', () => {
    const factory = vi.fn(() => ({ onText: vi.fn(), onBinary: vi.fn(), dispose: vi.fn() }));
    const { sessions, sockets } = setup({ escritorio: factory });
    sessions.open({ ...open, tipo: 'escritorio', tecnico: 'Luis Peña', pedir_permiso: true });
    sockets[0]!.onopen?.({});

    expect(factory).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 12, technician: 'Luis Peña', askConsent: true }),
    );
  });

  it('avisa al técnico si el tipo de sesión todavía no está disponible', () => {
    const { sessions, sockets } = setup();
    sessions.open({ ...open, tipo: 'escritorio' });
    sockets[0]!.onopen?.({});

    expect(String(sockets[0]!.sent[1])).toContain('todavía no admite sesiones de tipo escritorio');
    expect(sockets[0]!.closed).toBe(true);
  });

  it('no se conecta a un relay fuera de su servidor', () => {
    const { sessions, sockets } = setup({ terminal: vi.fn() });
    sessions.open({ ...open, url_relay: 'https://otro.servidor/relay/12' });
    sessions.open({ ...open, url_relay: '/relay/12/../../agent' });

    expect(sockets).toHaveLength(0);
  });
});

import { act, cleanup, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type SessionTicket } from '@monitoreoti/shared';
import { fakePanelApi, fakeRemoteApi, renderWithPanel } from '../../test/fakes';
import { relayUrl, RemoteSessionDialog } from './RemoteSession';
import { RemotoPage } from './RemotoPage';

afterEach(cleanup);

const ticket: SessionTicket = {
  sessionId: 12,
  type: 'terminal',
  machineId: 3,
  machineName: 'CONTA-03',
  relayPath: '/relay/12',
  token: 'tok-panel',
  expiresAt: '2026-10-31T12:01:00Z',
};

class FakeRelay {
  sent: string[] = [];
  closed = false;
  binaryType = 'blob';
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  constructor(readonly url: string) {}
  send(data: string) {
    this.sent.push(data);
  }
  close() {
    this.closed = true;
    this.onclose?.();
  }
}

function relayFactory() {
  const sockets: FakeRelay[] = [];
  const create = vi.fn((url: string) => {
    const socket = new FakeRelay(url);
    sockets.push(socket);
    return socket as unknown as WebSocket;
  });
  return { sockets, create };
}

describe('sesión remota (W-29)', () => {
  it('arma la URL del relay en el mismo servidor', () => {
    expect(relayUrl('https://api.monitoreoti.do', '/relay/12')).toBe('wss://api.monitoreoti.do/relay/12');
    expect(relayUrl('http://localhost:5126', '/relay/12')).toBe('ws://localhost:5126/relay/12');
  });

  it('manda el token al relay, espera al equipo y muestra el motivo si el agente no puede atenderla', async () => {
    const { sockets, create } = relayFactory();
    const closeSession = vi.fn(() => Promise.resolve({ id: 12 }));
    const view = renderWithPanel(
      <RemoteSessionDialog
        machineId={3}
        machineName="CONTA-03"
        type="terminal"
        onClose={vi.fn()}
        createSocket={create}
      />,
      fakePanelApi({ remoto: fakeRemoteApi({ openSession: () => Promise.resolve(ticket), closeSession }) }),
    );

    expect(await screen.findByText('Esperando a que el equipo se conecte…')).toBeTruthy();
    const socket = sockets[0]!;
    act(() => socket.onopen?.());
    expect(socket.sent).toEqual([JSON.stringify({ token: 'tok-panel' })]);

    act(() =>
      socket.onmessage?.({
        data: JSON.stringify({ tipo: 'error', mensaje: 'Este agente todavía no admite sesiones de tipo terminal.' }),
      }),
    );
    expect(screen.getByText('No se pudo conectar.')).toBeTruthy();
    expect(screen.getByText(/todavía no admite sesiones de tipo terminal/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reintentar' })).toBeTruthy();

    // Al salir de la pantalla la sesión se cierra también en el servidor.
    view.unmount();
    expect(closeSession).toHaveBeenCalledWith(12);
  });

  it('entrega el socket al visor cuando el relay confirma la conexión', async () => {
    const { sockets, create } = relayFactory();
    renderWithPanel(
      <RemoteSessionDialog machineId={3} machineName="CONTA-03" type="terminal" onClose={vi.fn()} createSocket={create}>
        {() => <p>Visor de la terminal</p>}
      </RemoteSessionDialog>,
      fakePanelApi({
        remoto: fakeRemoteApi({
          openSession: () => Promise.resolve(ticket),
          closeSession: vi.fn(() => Promise.resolve({ id: 12 })),
        }),
      }),
    );

    await waitFor(() => expect(sockets).toHaveLength(1));
    act(() => sockets[0]!.onmessage?.({ data: JSON.stringify({ relay: 'conectado' }) }));
    expect(screen.getByText('Visor de la terminal')).toBeTruthy();
  });

  it('entrega el ticket a la ventana de la terminal sin conectarse aquí', async () => {
    const { sockets, create } = relayFactory();
    const handoff = vi.fn(() => true);
    const onClose = vi.fn();
    const closeSession = vi.fn(() => Promise.resolve({ id: 12 }));
    renderWithPanel(
      <RemoteSessionDialog
        machineId={3}
        machineName="CONTA-03"
        type="terminal"
        onClose={onClose}
        createSocket={create}
        handoff={handoff}
      />,
      fakePanelApi({ remoto: fakeRemoteApi({ openSession: () => Promise.resolve(ticket), closeSession }) }),
    );

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(handoff).toHaveBeenCalledWith(ticket);
    expect(sockets).toHaveLength(0);
    // La sesión sigue viva: la cierra la ventana de la terminal.
    expect(closeSession).not.toHaveBeenCalled();
  });

  it('pide el 2FA si el servidor lo exige', async () => {
    renderWithPanel(
      <RemoteSessionDialog
        machineId={3}
        machineName="CONTA-03"
        type="archivos"
        onClose={vi.fn()}
        createSocket={vi.fn()}
      />,
      fakePanelApi({
        remoto: fakeRemoteApi({ openSession: () => Promise.reject(new ApiError(403, '2fa_requerido', 'Confirme.')) }),
      }),
    );

    expect(await screen.findByRole('dialog', { name: 'Confirme su identidad' })).toBeTruthy();
  });
});

describe('historial de sesiones (W-34)', () => {
  it('lista cada sesión con su duración', async () => {
    renderWithPanel(
      <RemotoPage canRun={false} canEdit={false} canSessions onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        remoto: fakeRemoteApi({
          sessions: () =>
            Promise.resolve({
              items: [
                {
                  id: 12,
                  type: 'terminal',
                  machineId: 3,
                  machineName: 'CONTA-03',
                  branchName: 'Santiago',
                  userId: 2,
                  userName: 'Luis Peña',
                  startedAt: '2026-10-31T09:00:00Z',
                  endedAt: '2026-10-31T09:12:00Z',
                  durationSeconds: 720,
                  sourceIp: '10.0.0.5',
                  active: false,
                  reason: 'Revisar impresora',
                  viewOnly: false,
                },
              ],
              total: 1,
              page: 1,
              pageSize: 50,
            }),
        }),
      }),
    );

    expect(await screen.findByText('CONTA-03')).toBeTruthy();
    expect(screen.getByText('12 min')).toBeTruthy();
    expect(screen.getByText('Luis Peña')).toBeTruthy();
  });
});

import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TerminalWindow } from './TerminalWindow';

vi.mock('./TerminalView', () => ({ TerminalView: () => <p>xterm</p> }));

afterEach(cleanup);

class FakeRelay {
  sent: string[] = [];
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  constructor(readonly url: string) {}
  send(data: string) {
    this.sent.push(data);
  }
  close() {
    this.onclose?.();
  }
}

describe('ventana de la terminal (W-30)', () => {
  it('sin ticket explica que hay que abrirla otra vez desde la ficha', async () => {
    render(<TerminalWindow loadTicket={() => Promise.resolve(null)} createSocket={vi.fn()} />);
    expect(await screen.findByText(/Ábrala de nuevo desde la ficha/)).toBeTruthy();
  });

  it('se conecta al relay con su ticket y muestra la terminal cuando llega el equipo', async () => {
    const sockets: FakeRelay[] = [];
    render(
      <TerminalWindow
        loadTicket={() =>
          Promise.resolve({
            sessionId: 12,
            kind: 'terminal',
            relayPath: '/relay/12',
            token: 'tok-panel',
            machineName: 'CONTA-03',
            serverUrl: 'http://localhost:5126',
          })
        }
        createSocket={(url) => {
          const socket = new FakeRelay(url);
          sockets.push(socket);
          return socket as unknown as WebSocket;
        }}
      />,
    );

    await waitFor(() => expect(sockets).toHaveLength(1));
    expect(sockets[0]!.url).toBe('ws://localhost:5126/relay/12');
    act(() => sockets[0]!.onopen?.());
    expect(sockets[0]!.sent).toEqual([JSON.stringify({ token: 'tok-panel' })]);
    act(() => sockets[0]!.onmessage?.({ data: JSON.stringify({ relay: 'conectado' }) }));
    expect(screen.getByText('xterm')).toBeTruthy();
    expect(screen.getByText('Terminal · CONTA-03')).toBeTruthy();
  });
});

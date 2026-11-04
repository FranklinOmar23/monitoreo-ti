import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakePanelApi, fakeRemoteApi, machineDetail, renderWithPanel } from '../../test/fakes';
import { ConnectDialog } from './ConnectDialog';
import { desktopEvent } from './DesktopView';
import { errorCauses } from './DesktopWindow';
import { FileClient } from './fileClient';

afterEach(cleanup);

describe('antes de conectar (W-28a)', () => {
  it('manda el modo, la calidad, el motivo y las opciones elegidas', async () => {
    const onConnect = vi.fn();
    renderWithPanel(
      <ConnectDialog
        machine={machineDetail({ name: 'CONTA-03', assignedTo: 'María Peña' })}
        onCancel={vi.fn()}
        onConnect={onConnect}
      />,
      fakePanelApi({ remoto: fakeRemoteApi() }),
    );

    fireEvent.click(screen.getByLabelText(/Solo ver/));
    fireEvent.change(screen.getByLabelText('Calidad'), { target: { value: '35' } });
    fireEvent.change(screen.getByLabelText(/Motivo de la sesión/), {
      target: { value: ' Respaldar disco · TCK-1042 ' },
    });
    fireEvent.click(screen.getByLabelText(/Pedir permiso a María Peña/));
    fireEvent.submit(document.getElementById('connect-desktop')!);

    expect(onConnect).toHaveBeenCalledWith({
      soloVer: true,
      calidad: 35,
      motivo: 'Respaldar disco · TCK-1042',
      pedirPermiso: true,
      sincronizarPortapapeles: true,
    });
  });

  it('si la empresa exige pedir permiso, no se puede apagar', async () => {
    const onConnect = vi.fn();
    renderWithPanel(
      <ConnectDialog machine={machineDetail({})} onCancel={vi.fn()} onConnect={onConnect} />,
      fakePanelApi({ remoto: fakeRemoteApi({ remoteSettings: () => Promise.resolve({ pedirPermiso: true }) }) }),
    );

    const toggle = (await screen.findByText(/su empresa lo exige/)).closest('label')!.querySelector('input')!;
    expect(toggle.checked).toBe(true);
    expect(toggle.disabled).toBe(true);
    fireEvent.submit(document.getElementById('connect-desktop')!);
    await waitFor(() => expect(onConnect).toHaveBeenCalledWith(expect.objectContaining({ pedirPermiso: true })));
  });
});

describe('visor del escritorio (W-28, W-29)', () => {
  it('traduce los mensajes del asistente', () => {
    expect(desktopEvent({ tipo: 'esperando_permiso', usuario: 'mpena', segundos: 30 })).toEqual({
      kind: 'permiso',
      user: 'mpena',
      seconds: 30,
    });
    expect(desktopEvent({ tipo: 'error', codigo: 'PERMISO_DENEGADO', mensaje: 'No permitió.' })).toEqual({
      kind: 'fin',
      message: 'No permitió.',
      code: 'PERMISO_DENEGADO',
    });
    expect(desktopEvent({ tipo: 'entrada_bloqueada', activo: true })).toEqual({
      kind: 'entrada_bloqueada',
      active: true,
    });
    expect(desktopEvent({ id: 3, ok: true })).toBeNull(); // respuesta del explorador de archivos
  });

  it('explica las causas probables según el error (W-29b)', () => {
    expect(errorCauses('ERR_RELAY_TIMEOUT', 'Santo Domingo', null).map((c) => c.text)).toEqual([
      'El equipo puede estar apagado o suspendido.',
      'La red de la sucursal Santo Domingo puede tener problemas.',
      'Un firewall local puede estar bloqueando el agente.',
    ]);
    expect(errorCauses('PERMISO_DENEGADO', undefined, 'María Peña')[0]!.text).toContain('María Peña rechazó');
    expect(errorCauses('SESION_TERMINADA', undefined, null)).toEqual([]);
  });
});

describe('archivos dentro del escritorio (W-28e)', () => {
  it('usa el marcador 2 en las partes y no confunde las imágenes de la pantalla', async () => {
    const sent: (string | Uint8Array)[] = [];
    const socket = Object.assign(new EventTarget(), { readyState: 1, send: (d: string | Uint8Array) => sent.push(d) });
    const client = new FileClient(socket as unknown as WebSocket, 2);
    const written: number[][] = [];
    const job = client.download(
      'C:\\a.txt',
      {
        write: (d) => {
          written.push([...d]);
          return Promise.resolve();
        },
        end: () => Promise.resolve(),
      },
      vi.fn(),
    );
    const message = (data: unknown) => socket.dispatchEvent(new MessageEvent('message', { data }));
    message(JSON.stringify({ id: 1, ok: true, tamano: 2 }));
    message(new Uint8Array([1, 0, 0, 0, 1, 9, 9]).buffer); // pantalla: se ignora
    message(new Uint8Array([2, 0, 0, 0, 1, 7, 8]).buffer); // parte del archivo 1
    message(JSON.stringify({ id: 1, fin: true }));
    await job.done;

    expect(written).toEqual([[7, 8]]);
  });
});

import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type BranchItem } from '@monitoreoti/shared';
import { counts, fakePanelApi, machineDetail, renderWithPanel, pageFrom } from '../test/fakes';
import { InstaladoresPage } from './InstaladoresPage';
import { MachineForm, RetireDialog } from './MachineForm';
import { QrPrintPage } from './QrPrintPage';
import { SucursalesPage } from './SucursalesPage';

afterEach(cleanup);

const santiago: BranchItem = {
  id: 2,
  name: 'Santiago',
  province: 'Santiago',
  address: null,
  managerId: null,
  managerName: null,
  isActive: true,
  counts: counts(),
};

describe('Formulario de equipo (W-13)', () => {
  it('agrega un equipo con los datos del formulario', async () => {
    const createMachine = vi.fn(() => Promise.resolve({ id: 42 }));
    const onSaved = vi.fn();
    renderWithPanel(
      <MachineForm onClose={vi.fn()} onSaved={onSaved} />,
      fakePanelApi({ branches: () => Promise.resolve([santiago]), createMachine }),
    );

    fireEvent.change(screen.getByLabelText('Nombre *'), { target: { value: ' IMP-CAJA ' } });
    fireEvent.change(screen.getByLabelText('Tipo *'), { target: { value: 'impresora' } });
    await screen.findByRole('option', { name: 'Santiago' });
    fireEvent.change(screen.getByLabelText('Sucursal *'), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('IP'), { target: { value: '10.20.30.40' } });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));

    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(42));
    expect(createMachine).toHaveBeenCalledWith(
      expect.objectContaining({ nombre: 'IMP-CAJA', tipo: 'impresora', sucursalId: 2, ip: '10.20.30.40', marca: null }),
    );
  });

  it('muestra el error del servidor sin cerrar el formulario', async () => {
    const onSaved = vi.fn();
    renderWithPanel(
      <MachineForm machine={machineDetail()} onClose={vi.fn()} onSaved={onSaved} />,
      fakePanelApi({
        branches: () => Promise.resolve([{ ...santiago, id: 1, name: 'Santo Domingo' }]),
        updateMachine: () => Promise.reject(new ApiError(400, undefined, 'La IP no es válida.')),
      }),
    );

    await screen.findByRole('option', { name: 'Santo Domingo' });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));

    expect((await screen.findByRole('alert')).textContent).toBe('La IP no es válida.');
    expect(onSaved).not.toHaveBeenCalled();
  });

  it('dar de baja pide confirmación', async () => {
    const retireMachine = vi.fn(() => Promise.resolve({ id: 1 }));
    const onRetired = vi.fn();
    renderWithPanel(
      <RetireDialog machine={machineDetail()} onClose={vi.fn()} onRetired={onRetired} />,
      fakePanelApi({ retireMachine }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Dar de baja' }));

    await waitFor(() => expect(onRetired).toHaveBeenCalled());
    expect(retireMachine).toHaveBeenCalledWith(1);
  });
});

describe('Etiquetas QR (W-14)', () => {
  it('muestra una etiqueta por equipo visible', async () => {
    renderWithPanel(
      <QrPrintPage machineIds={[1, 2]} onBack={vi.fn()} />,
      fakePanelApi({
        qrLabels: () =>
          Promise.resolve([
            { id: 1, nombre: 'CONTA-03', sucursal: 'Santo Domingo', codigo: 'x', png: 'data:image/png;base64,AA' },
          ]),
      }),
    );

    expect(await screen.findByAltText('Código QR de CONTA-03')).toBeTruthy();
  });
});

describe('Sucursales (W-15 a W-17)', () => {
  it('lista las sucursales y abre su detalle', async () => {
    const onOpenMachines = vi.fn();
    renderWithPanel(
      <SucursalesPage canEdit onOpenMachines={onOpenMachines} />,
      fakePanelApi({ branches: () => Promise.resolve([santiago]) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Santiago/ }));
    fireEvent.click(screen.getByRole('button', { name: /Ver sus 5 equipos/ }));

    expect(onOpenMachines).toHaveBeenCalledWith(2);
  });

  it('sin permiso de edición no ofrece agregar', async () => {
    renderWithPanel(
      <SucursalesPage canEdit={false} onOpenMachines={vi.fn()} />,
      fakePanelApi({ branches: () => Promise.resolve([santiago]) }),
    );

    await screen.findByRole('button', { name: /Santiago/ });
    expect(screen.queryByRole('button', { name: 'Agregar sucursal' })).toBeNull();
  });
});

describe('Instaladores (W-18, W-19)', () => {
  it('crea un token y lo muestra una sola vez con el comando de instalación', async () => {
    const createInstaller = vi.fn(() =>
      Promise.resolve({
        id: 5,
        token: '1.SECRETO',
        vence: new Date().toISOString(),
        urlServidor: 'https://api.monitoreoti.do',
        comando: 'MonitoreoTI-Agente-Setup.exe /S /SERVER=https://api.monitoreoti.do /TOKEN=1.SECRETO',
      }),
    );
    renderWithPanel(
      <InstaladoresPage />,
      fakePanelApi({ branches: () => Promise.resolve([santiago]), createInstaller }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Nuevo token de instalación' }));
    await screen.findByRole('option', { name: 'Santiago' });
    fireEvent.change(screen.getByLabelText('Sucursal *'), { target: { value: '2' } });
    fireEvent.click(screen.getByLabelText(/pendientes hasta que alguien los apruebe/));
    fireEvent.click(screen.getByRole('button', { name: 'Crear token' }));

    expect(await screen.findByText('1.SECRETO')).toBeTruthy();
    expect(screen.getByText(/\/S \/SERVER=/)).toBeTruthy();
    expect(createInstaller).toHaveBeenCalledWith(
      expect.objectContaining({ sucursalId: 2, diasVigencia: 7, requiereAprobacion: true, usosMax: null }),
    );
  });

  it('copia el token y la dirección por el proceso principal (el panel no tiene permiso de portapapeles)', async () => {
    const copyText = vi.fn(() => Promise.resolve());
    window.monitoreo = {
      getAppInfo: () => Promise.resolve({ name: 'x', version: '0', serverUrl: 'http://localhost:5126' }),
      session: { load: () => Promise.resolve(null), save: () => Promise.resolve(), clear: () => Promise.resolve() },
      saveFile: () => Promise.resolve({ saved: false }),
      copyText,
      notify: () => Promise.resolve(),
      onNotificationClick: () => () => undefined,
      openTerminal: () => Promise.resolve(),
      terminalTicket: () => Promise.resolve(null),
      readText: () => Promise.resolve(''),
      requestSession: () => Promise.resolve(false),
      onSessionRequest: () => () => undefined,
      // El explorador de archivos no se usa en estas pruebas.
      files: {} as never,
      view3d: {} as never,
      deepLinks: { take: () => Promise.resolve(null), onOpen: () => () => undefined },
      tray: { setCriticalCount: () => Promise.resolve(), onNavigate: () => () => undefined },
      updates: {
        status: () => Promise.resolve(null),
        onReady: () => () => undefined,
        install: () => Promise.resolve(),
      },
    };
    try {
      renderWithPanel(
        <InstaladoresPage />,
        fakePanelApi({
          branches: () => Promise.resolve([santiago]),
          createInstaller: () =>
            Promise.resolve({
              id: 5,
              token: '1.SECRETO',
              vence: new Date().toISOString(),
              urlServidor: 'http://localhost:5126',
              comando: 'MonitoreoTI-Agente-Setup.exe /S /SERVER=http://localhost:5126 /TOKEN=1.SECRETO',
            }),
        }),
      );
      fireEvent.click(screen.getByRole('button', { name: 'Nuevo token de instalación' }));
      await screen.findByRole('option', { name: 'Santiago' });
      fireEvent.change(screen.getByLabelText('Sucursal *'), { target: { value: '2' } });
      fireEvent.click(screen.getByRole('button', { name: 'Crear token' }));

      fireEvent.click(await screen.findByRole('button', { name: 'Copiar' }));
      fireEvent.click(screen.getByRole('button', { name: 'Copiar dirección' }));

      await waitFor(() => expect(copyText).toHaveBeenCalledWith('http://localhost:5126'));
      expect(copyText).toHaveBeenCalledWith('1.SECRETO');
    } finally {
      delete window.monitoreo;
    }
  });

  it('elimina un token después de confirmar', async () => {
    const deleteInstaller = vi.fn(() => Promise.resolve({ id: 3 }));
    renderWithPanel(
      <InstaladoresPage />,
      fakePanelApi({
        deleteInstaller,
        installers: pageFrom(() =>
          Promise.resolve([
            {
              id: 3,
              name: 'cvaja',
              branchId: 1,
              branchName: 'Santo Domingo',
              expiresAt: new Date().toISOString(),
              maxUses: 10,
              uses: 0,
              requiresApproval: false,
              revokedAt: new Date().toISOString(),
              createdAt: new Date().toISOString(),
              createdBy: 'Ana Martínez',
              state: 'revocado',
            },
          ])),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Eliminar' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Eliminar' }));

    await waitFor(() => expect(deleteInstaller).toHaveBeenCalledWith(3));
  });

  it('aprueba un equipo pendiente', async () => {
    const approveAgent = vi.fn(() => Promise.resolve({ id: 1 }));
    renderWithPanel(
      <InstaladoresPage />,
      fakePanelApi({
        approveAgent,
        pendingAgents: pageFrom(() =>
          Promise.resolve([
            {
              agentId: 9,
              machineId: 3,
              machineName: 'CAJA-07',
              ipAddress: '10.0.0.7',
              macAddress: null,
              operatingSystem: null,
              branchName: 'Santiago',
              registeredAt: new Date().toISOString(),
            },
          ])),
      }),
    );

    fireEvent.click(await screen.findByRole('tab', { name: /Pendientes de aprobación/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Aprobar' }));

    await waitFor(() => expect(approveAgent).toHaveBeenCalledWith(9));
  });
});

import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type InstallerToken, type RuleItem, type RunDetail } from '@monitoreoti/shared';
import { fakePanelApi, fakeRemoteApi, renderWithPanel } from '../test/fakes';
import { InstaladoresPage } from './InstaladoresPage';
import { ReglasPage } from './ReglasPage';
import { StartServiceButton } from './remoto/StartServiceButton';

afterEach(cleanup);

const run = (
  status: 'pendiente' | 'completado' | 'error',
  exitCode: number | null,
  output: string | null,
): RunDetail => ({
  summary: {
    batchId: 'b1',
    scriptId: null,
    scriptName: null,
    language: 'powershell',
    userName: 'Ana',
    createdAt: new Date().toISOString(),
    total: 1,
    pending: status === 'pendiente' ? 1 : 0,
    running: 0,
    completed: status === 'completado' ? 1 : 0,
    failed: status === 'error' ? 1 : 0,
  } as unknown as RunDetail['summary'],
  content: '',
  timeoutSeconds: 60,
  commands: [
    {
      id: 1,
      batchId: 'b1',
      machineId: 7,
      machineName: 'PC-042077',
      branchName: 'Santo Domingo',
      status,
      exitCode,
      createdAt: new Date().toISOString(),
      sentAt: null,
      completedAt: null,
      output,
    },
  ],
});

describe('Reparar servicios (decisión 73)', () => {
  it('"Iniciar servicio" manda el comando y muestra que arrancó', async () => {
    const startAlertService = vi.fn(() => Promise.resolve({ lote: 'b1' }));
    renderWithPanel(
      <StartServiceButton alertId={5} service="Spooler" />,
      fakePanelApi({
        startAlertService,
        remoto: fakeRemoteApi({ runDetail: () => Promise.resolve(run('completado', 0, 'Running')) }),
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Iniciar Spooler' }));

    expect(await screen.findByRole('button', { name: 'Servicio iniciado' })).toBeTruthy();
    expect(startAlertService).toHaveBeenCalledWith(5);
  });

  it('si el servicio no arranca, dice por qué', async () => {
    renderWithPanel(
      <StartServiceButton alertId={5} service="Spooler" />,
      fakePanelApi({
        startAlertService: () => Promise.resolve({ lote: 'b1' }),
        remoto: fakeRemoteApi({
          runDetail: () => Promise.resolve(run('error', 1, 'Start-Service: el servicio no se pudo iniciar')),
        }),
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Iniciar Spooler' }));

    expect((await screen.findByRole('alert')).textContent).toContain('no se pudo iniciar');
  });

  it('pide el código de verificación si el 2FA no es reciente', async () => {
    renderWithPanel(
      <StartServiceButton alertId={5} service="Spooler" />,
      fakePanelApi({
        startAlertService: () => Promise.reject(new ApiError(403, '2fa_requerido', 'Confirme su código')),
      }),
    );

    fireEvent.click(screen.getByRole('button', { name: 'Iniciar Spooler' }));

    expect(await screen.findByRole('dialog', { name: 'Confirme su identidad' })).toBeTruthy();
  });

  it('un token activo se puede volver a ver', async () => {
    const token: InstallerToken = {
      id: 9,
      name: 'Nuev',
      branchId: 1,
      branchName: 'Santo Domingo',
      expiresAt: new Date(Date.now() + 86_400_000).toISOString(),
      maxUses: null,
      uses: 1,
      requiresApproval: false,
      revokedAt: null,
      createdAt: new Date().toISOString(),
      createdBy: 'Ana',
      state: 'activo',
      canShowCommand: true,
    };
    const installerCommand = vi.fn(() =>
      Promise.resolve({
        id: 9,
        token: '1.OTRAVEZ',
        vence: token.expiresAt,
        urlServidor: 'https://192.168.3.115:5127',
        comando: 'MonitoreoTI-Agente-Setup.exe /S /SERVER=https://192.168.3.115:5127 /TOKEN=1.OTRAVEZ',
      }),
    );
    renderWithPanel(
      <InstaladoresPage />,
      fakePanelApi({ installers: () => Promise.resolve([token]), installerCommand }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Ver comando' }));

    const dialog = await screen.findByRole('dialog', { name: 'Comando de instalación' });
    expect(within(dialog).getByText('1.OTRAVEZ')).toBeTruthy();
    expect(installerCommand).toHaveBeenCalledWith(9);
  });

  it('la regla de servicio detenido activa la reparación automática', async () => {
    const rule: RuleItem = {
      id: null,
      metric: 'servicio_detenido',
      name: 'Servicio automático detenido',
      description: 'Un servicio que arranca con Windows estaba en ejecución y se detuvo.',
      unit: '',
      lessIsWorse: false,
      hasThresholds: false,
      hasDuration: false,
      available: true,
      warning: null,
      critical: 1,
      durationMinutes: 0,
      isActive: true,
      overrides: [],
      autoRepair: false,
      canAutoRepair: true,
    };
    const saveRule = vi.fn(() => Promise.resolve({ id: 1 }));
    renderWithPanel(<ReglasPage canManage />, fakePanelApi({ rules: () => Promise.resolve([rule]), saveRule }));

    fireEvent.click(await screen.findByRole('button', { name: 'Ajustar' }));
    fireEvent.click(await screen.findByLabelText(/Iniciar automáticamente los servicios/));
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));

    await waitFor(() =>
      expect(saveRule).toHaveBeenCalledWith(
        expect.objectContaining({ metrica: 'servicio_detenido', repararAuto: true }),
      ),
    );
  });
});

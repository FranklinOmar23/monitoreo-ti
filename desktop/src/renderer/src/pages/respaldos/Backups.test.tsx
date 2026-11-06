import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type BackupJobItem, type BackupOverview, type MachineDetail } from '@monitoreoti/shared';
import { fakeBackupsApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { BackupsTab } from './BackupsTab';

afterEach(cleanup);

const now = new Date().toISOString();
const machine = { id: 9, name: 'SRV-SD-01', type: 'servidor' } as MachineDetail;

const job = (overrides: Partial<BackupJobItem> = {}): BackupJobItem => ({
  id: 1,
  name: 'ERP a disco externo',
  source: 'sql',
  sqlInstance: 'MSSQLSERVER',
  items: ['ERP_Caribe'],
  mode: 'caliente',
  stopServices: [],
  coldMaxMinutes: 60,
  strategy: 'incremental',
  fullEveryDays: 7,
  destinationType: 'disco_externo',
  destinationPath: 'Centinela',
  disks: [{ serie: 'WD-123', nombre: 'WD Elements' }],
  ejectAfter: false,
  networkUser: null,
  hasNetworkPassword: false,
  frequency: 'diario',
  weekDays: [],
  time: '23:00',
  keepDaily: 7,
  keepMonthly: 3,
  verify: true,
  isActive: true,
  lastRunAt: now,
  lastStatus: 'correcto',
  lastSuccessAt: now,
  lastError: null,
  nextRunAt: now,
  diskMissingSince: null,
  servicesNotRestarted: null,
  createdAt: now,
  ...overrides,
});

const overview = (overrides: Partial<BackupOverview> = {}): BackupOverview => ({
  machineId: 9,
  machineName: 'SRV-SD-01',
  isServer: true,
  typeLocked: false,
  agentVersion: '0.4.0',
  agentConnected: true,
  agentSupportsBackups: true,
  okLast30Days: 12,
  failedLast30Days: 1,
  jobs: [job()],
  runs: [
    {
      id: 5,
      jobId: 1,
      jobName: 'ERP a disco externo',
      trigger: 'programado',
      requestedBy: null,
      status: 'correcto',
      kind: 'diferencial',
      startedAt: now,
      finishedAt: now,
      bytesCopied: 312 * 1024 * 1024,
      bytesTotal: 48 * 1024 ** 3,
      files: 1,
      destination: 'Disco WD Elements (E:)',
      setName: '20261105-230000-diferencial',
      detail: 'Diferencial: 312 MB de 48 GB.',
    },
  ],
  ...overrides,
});

describe('Respaldos de servidores (decisión 82)', () => {
  it('un equipo que no es servidor explica cómo marcarlo', async () => {
    renderWithPanel(
      <BackupsTab machine={machine} canManage />,
      fakePanelApi({ respaldos: fakeBackupsApi({ overview: () => Promise.resolve(overview({ isServer: false, jobs: [], runs: [] })) }) }),
    );
    expect(await screen.findByText('Los respaldos son para servidores')).toBeTruthy();
  });

  it('muestra los trabajos, lo copiado de verdad en el historial y los problemas', async () => {
    renderWithPanel(
      <BackupsTab machine={machine} canManage={false} />,
      fakePanelApi({
        respaldos: fakeBackupsApi({
          overview: () =>
            Promise.resolve(
              overview({
                jobs: [
                  job(),
                  job({ id: 2, name: 'Documentos', source: 'carpetas', items: ['D:\\Datos'], lastStatus: 'error', lastError: 'Acceso denegado al NAS.' }),
                ],
              }),
            ),
        }),
      }),
    );
    const jobs = (await screen.findByRole('heading', { name: 'Trabajos de respaldo' })).closest('section')!;
    expect(within(jobs).getByText('ERP a disco externo')).toBeTruthy();
    expect(within(jobs).getAllByText(/Solo lo nuevo \(completa cada 7 días\)/)).toHaveLength(2);
    expect(screen.getByRole('alert').textContent).toContain('Acceso denegado al NAS.');
    const history = screen.getByRole('heading', { name: 'Historial' }).closest('section')!;
    expect(within(history).getByText('312 MB de 48.0 GB')).toBeTruthy();
    expect(within(history).getByText('Diferencial')).toBeTruthy();
    // Sin permiso, no hay botones de gestión.
    expect(screen.queryByRole('button', { name: 'Respaldar ahora' })).toBeNull();
  });

  it('Respaldar ahora pide el 2FA si hace falta y luego lo manda', async () => {
    const runNow = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(403, '2fa_requerido', 'Confirme su código.'))
      .mockResolvedValue({ id: 8 });
    renderWithPanel(
      <BackupsTab machine={machine} canManage />,
      fakePanelApi({ respaldos: fakeBackupsApi({ overview: () => Promise.resolve(overview()), runNow }) }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Hacer completo ahora' }));
    await waitFor(() => expect(runNow).toHaveBeenCalledWith(9, 1, true));
    expect(await screen.findByText('Confirme su identidad')).toBeTruthy();
  });

  it('crea un respaldo eligiendo la base y el disco que leyó el agente', async () => {
    const save = vi.fn(() => Promise.resolve({ id: 3 }));
    renderWithPanel(
      <BackupsTab machine={machine} canManage />,
      fakePanelApi({
        respaldos: fakeBackupsApi({
          overview: () => Promise.resolve(overview({ jobs: [], runs: [] })),
          explore: () =>
            Promise.resolve({
              ok: true,
              error: null,
              instances: [
                { name: 'MSSQLSERVER', error: null, databases: [{ name: 'ERP_Caribe', sizeMb: 49152, recoveryModel: 'SIMPLE' }] },
              ],
              services: [{ name: 'ERPService', displayName: 'Servicio del ERP', state: 'en_ejecucion' }],
              disks: [{ serial: 'WD-123', name: 'WD Elements', letter: 'E:', totalGb: 1863, freeGb: 900 }],
            }),
          save,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Nuevo respaldo/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Nombre *'), { target: { value: 'ERP diario' } });
    fireEvent.click(await within(dialog).findByRole('checkbox', { name: /ERP_Caribe/ }));
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /WD Elements/ }));
    fireEvent.click(within(dialog).getByRole('radio', { name: /En frío/ }));
    fireEvent.click(within(dialog).getByRole('checkbox', { name: /Servicio del ERP/ }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Guardar' }));

    await waitFor(() =>
      expect(save).toHaveBeenCalledWith(
        9,
        null,
        expect.objectContaining({
          nombre: 'ERP diario',
          origen: 'sql',
          instanciaSql: 'MSSQLSERVER',
          elementos: ['ERP_Caribe'],
          modo: 'frio',
          serviciosDetener: ['ERPService'],
          estrategia: 'incremental',
          completoCadaDias: 7,
          destinoTipo: 'disco_externo',
          destinoRuta: 'Centinela',
          discos: [{ serie: 'WD-123', nombre: 'WD Elements' }],
          redClave: null,
          hora: '23:00',
        }),
      ),
    );
  });
});

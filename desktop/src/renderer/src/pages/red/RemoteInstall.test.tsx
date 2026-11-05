import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type IpItem, type RemoteInstallItem, type SubnetDetail, type SubnetItem } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { RedPage } from './RedPage';

afterEach(cleanup);

const now = new Date().toISOString();

const subnet: SubnetItem = {
  id: 4,
  name: 'VLAN4',
  branchId: 1,
  branchName: 'Santo Domingo',
  cidr: '192.168.4.0/29',
  vlan: 4,
  gateway: '192.168.4.3',
  collectorId: 8,
  collectorName: 'PC-042076',
  collectorOnline: true,
  collectorCanSweep: true,
  arpDeviceId: 8,
  arpDeviceName: 'FGT-SD-02',
  sweepHours: 0,
  freeAfterDays: 30,
  lastSweepAt: now,
  sweepRequestedAt: null,
  lastError: null,
  total: 6,
  used: 3,
  unidentified: 2,
  recent: 0,
  reserved: 1,
  free: 2,
};

const ip = (overrides: Partial<IpItem>): IpItem => ({
  ip: '192.168.4.0',
  state: 'libre',
  mac: null,
  vendor: null,
  machineId: null,
  machineName: null,
  machineSource: null,
  port: null,
  firstSeenAt: null,
  lastSeenAt: null,
  lastPingAt: null,
  reservation: null,
  isGateway: false,
  macChanged: false,
  previousMac: null,
  ...overrides,
});

const detail: SubnetDetail = {
  subnet,
  addresses: [
    ip({
      ip: '192.168.4.1',
      state: 'en_uso',
      machineId: 42,
      machineName: 'PC-042076',
      machineSource: 'agente',
      lastSeenAt: now,
    }),
    ip({ ip: '192.168.4.2', state: 'libre' }),
    ip({ ip: '192.168.4.3', state: 'reservada', isGateway: true, reservation: 'Puerta de enlace' }),
    ip({
      ip: '192.168.4.6',
      state: 'sin_identificar',
      lastSeenAt: now,
      hostname: 'PC-MONITOREO003',
      hostnameSource: 'netbios',
      operatingSystem: 'Windows 10/11',
    }),
    ip({ ip: '192.168.4.5', state: 'sin_identificar', lastSeenAt: now, vendor: 'Yealink' }),
  ],
  reservations: [],
  suggested: '192.168.4.2',
};

const install = (overrides: Partial<RemoteInstallItem>): RemoteInstallItem => ({
  id: 1,
  ip: '192.168.4.6',
  status: 'instalando',
  detail: null,
  operatingSystem: 'Windows 10 (10.0.19045) 64 bits',
  account: 'CARIBE\\soporte',
  userName: 'Ana Martínez',
  machineId: null,
  machineName: null,
  createdAt: now,
  updatedAt: now,
  ...overrides,
});

describe('Instalación remota del agente (decisión 77)', () => {
  it('desde el detalle de una IP sin agente se instala con la cuenta del dominio; pide el 2FA si hace falta', async () => {
    const installAgents = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(403, '2fa_requerido', 'Confirme su código.'))
      .mockResolvedValue({ id: 1 });
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({
          subnets: () => Promise.resolve([subnet]),
          subnet: () => Promise.resolve(detail),
          installAgents,
        }),
      }),
    );

    await screen.findByRole('heading', { name: '192.168.4.0/29' });
    const panel = screen.getByRole('heading', { name: 'Detalle' }).closest('section')!;
    // La del agente ofrece reinstalar (si el servicio no quedó instalado); la puerta de enlace, nada.
    fireEvent.click(screen.getByRole('button', { name: /192\.168\.4\.1: En uso/ }));
    expect(within(panel).queryByRole('button', { name: 'Instalar agente' })).toBeNull();
    expect(within(panel).getByRole('button', { name: 'Reinstalar agente' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /192\.168\.4\.3: Reservada/ }));
    expect(within(panel).queryByRole('button', { name: /instalar agente/i })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /192\.168\.4\.6: Sin identificar/ }));
    fireEvent.click(within(panel).getByRole('button', { name: 'Instalar agente' }));
    const dialog = await screen.findByRole('dialog');
    expect((within(dialog).getByLabelText(/^Direcciones IP/) as HTMLTextAreaElement).value).toBe('192.168.4.6');
    expect(within(dialog).getByText('PC-042076')).toBeTruthy();
    fireEvent.change(within(dialog).getByLabelText('Cuenta del dominio *'), { target: { value: 'CARIBE\\soporte' } });
    fireEvent.change(within(dialog).getByLabelText('Clave *'), { target: { value: 'Clave-1' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Instalar' }));

    await waitFor(() =>
      expect(installAgents).toHaveBeenCalledWith(4, {
        ips: ['192.168.4.6'],
        cuenta: 'CARIBE\\soporte',
        clave: 'Clave-1',
      }),
    );
    expect(await screen.findByText('Confirme su identidad')).toBeTruthy();
  });

  it('"Instalar agente…" propone las IP sin identificar que parecen Windows', async () => {
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({ subnets: () => Promise.resolve([subnet]), subnet: () => Promise.resolve(detail) }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Instalar agente…' }));
    const dialog = await screen.findByRole('dialog');
    // La .5 (un teléfono Yealink) no se propone.
    expect((within(dialog).getByLabelText(/^Direcciones IP/) as HTMLTextAreaElement).value).toBe('192.168.4.6');
  });

  it('la tabla muestra cómo va cada instalación y el equipo que quedó registrado', async () => {
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={onOpenEquipo} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({
          subnets: () => Promise.resolve([subnet]),
          subnet: () => Promise.resolve(detail),
          remoteInstalls: () =>
            Promise.resolve([
              install({
                id: 3,
                ip: '192.168.4.6',
                status: 'registrado',
                machineId: 77,
                machineName: 'PC-MONITOREO003',
              }),
              install({
                id: 2,
                ip: '192.168.4.9',
                status: 'omitido',
                operatingSystem: 'Windows 7 (6.1.7601) 64 bits',
                detail: 'Windows 7: el agente actual necesita Windows 10 o superior.',
              }),
              install({ id: 1, ip: '192.168.4.10', status: 'error', detail: 'Usuario o clave incorrectos.' }),
            ]),
        }),
      }),
    );

    const card = (await screen.findByRole('heading', { name: 'Instalaciones del agente' })).closest('section')!;
    expect(within(card).getByText('Registrado')).toBeTruthy();
    expect(within(card).getByText('Usuario o clave incorrectos.')).toBeTruthy();
    expect(within(card).getByText(/Windows 7: el agente actual/)).toBeTruthy();
    fireEvent.click(within(card).getByRole('button', { name: 'PC-MONITOREO003' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(77);
  });
});

import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IpItem, SubnetDetail, SubnetItem } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { RedPage } from './RedPage';

afterEach(cleanup);

const subnet: SubnetItem = {
  id: 3,
  name: 'Datos Santo Domingo',
  branchId: 1,
  branchName: 'Santo Domingo',
  cidr: '192.168.3.0/29',
  vlan: 10,
  gateway: '192.168.3.1',
  collectorId: 7,
  collectorName: 'CONTA-01',
  collectorOnline: true,
  collectorCanSweep: true,
  arpDeviceId: 2,
  arpDeviceName: 'FGT-SD-01',
  sweepHours: 24,
  freeAfterDays: 30,
  lastSweepAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
  sweepRequestedAt: null,
  lastError: null,
  total: 6,
  used: 2,
  unidentified: 1,
  recent: 1,
  reserved: 1,
  free: 2,
};

const ip = (overrides: Partial<IpItem>): IpItem => ({
  ip: '192.168.3.0',
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
    ip({ ip: '192.168.3.1', state: 'reservada', reservation: 'Puerta de enlace', isGateway: true }),
    ip({
      ip: '192.168.3.2',
      state: 'en_uso',
      machineId: 42,
      machineName: 'PC-042076',
      machineSource: 'agente',
      lastSeenAt: new Date().toISOString(),
    }),
    ip({
      ip: '192.168.3.3',
      state: 'sin_identificar',
      mac: '80:5E:C0:4A:12:9F',
      vendor: 'Yealink',
      port: 'SW-SD-01 p12',
      lastSeenAt: new Date().toISOString(),
      lastPingAt: new Date().toISOString(),
      macChanged: true,
      previousMac: '80:5E:C0:00:00:01',
      hostname: 'TEL-RECEPCION',
      hostnameSource: 'netbios',
      interface: 'VLAN3-x3',
    }),
    ip({ ip: '192.168.3.4', state: 'reciente', lastSeenAt: new Date(Date.now() - 3 * 86_400_000).toISOString() }),
    ip({ ip: '192.168.3.5' }),
    ip({ ip: '192.168.3.6' }),
  ],
  reservations: [],
  suggested: '192.168.3.5',
};

describe('Direcciones IP (decisión 72)', () => {
  it('muestra la subred, la IP libre sugerida y el detalle de cada dirección', async () => {
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={onOpenEquipo} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({ subnets: () => Promise.resolve([subnet]), subnet: () => Promise.resolve(detail) }),
      }),
    );

    expect(await screen.findByRole('heading', { name: '192.168.3.0/29' })).toBeTruthy();
    expect(screen.getByText('CONTA-01')).toBeTruthy();
    const suggestion = screen.getByText('IP libre sugerida').closest('div')!;
    expect(within(suggestion).getByText('192.168.3.5')).toBeTruthy();

    // Al entrar, el detalle muestra la sugerida.
    const panel = screen.getByRole('heading', { name: 'Detalle' }).closest('section')!;
    expect(within(panel).getByText(/se puede asignar/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /192\.168\.3\.3: Sin identificar/ }));
    expect(within(panel).getByText('Yealink')).toBeTruthy();
    expect(within(panel).getByText('SW-SD-01 p12')).toBeTruthy();
    expect(within(panel).getByText('TEL-RECEPCION (lo dio el equipo)')).toBeTruthy();
    expect(within(panel).getByText('VLAN3-x3')).toBeTruthy();
    expect(within(panel).getByText(/Cambió de MAC/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /192\.168\.3\.2: En uso, PC-042076/ }));
    fireEvent.click(within(panel).getByRole('button', { name: 'PC-042076' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(42);
  });

  it('la leyenda apaga un estado en el mapa', async () => {
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({ subnets: () => Promise.resolve([subnet]), subnet: () => Promise.resolve(detail) }),
      }),
    );

    const free = await screen.findByRole('button', { name: '192.168.3.5: Libre' });
    expect(free.getAttribute('data-dim')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Libre', pressed: true }));
    expect(free.getAttribute('data-dim')).not.toBeNull();
    // Sin permiso de edición no hay botones para cambiar la subred.
    expect(screen.queryByRole('button', { name: 'Escanear ahora' })).toBeNull();
  });

  it('"Escanear ahora" pide el barrido y muestra el error si el recolector no está', async () => {
    const sweepSubnet = vi.fn(() =>
      Promise.reject(new Error('El recolector no está conectado: inténtelo cuando vuelva a estar en línea.')),
    );
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({
          subnets: () => Promise.resolve([subnet]),
          subnet: () => Promise.resolve(detail),
          sweepSubnet,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Escanear ahora' }));

    await waitFor(() => expect(sweepSubnet).toHaveBeenCalledWith(3));
    expect((await screen.findByRole('alert')).textContent).toContain('El recolector no está conectado');
  });

  it('el detalle muestra quién tuvo la IP en los últimos días', async () => {
    const ipHistory = vi.fn(() =>
      Promise.resolve([
        {
          mac: '80:5E:C0:4A:12:9F',
          vendor: 'Yealink',
          machineId: null,
          machineName: null,
          source: 'arp' as const,
          from: new Date(Date.now() - 86_400_000).toISOString(),
          to: new Date().toISOString(),
          current: true,
        },
        {
          mac: '3C:52:82:1A:7F:04',
          vendor: 'HP',
          machineId: 42,
          machineName: 'PC-042076',
          source: 'agente' as const,
          from: new Date(Date.now() - 20 * 86_400_000).toISOString(),
          to: new Date(Date.now() - 10 * 86_400_000).toISOString(),
          current: false,
        },
      ]),
    );
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={onOpenEquipo} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({
          subnets: () => Promise.resolve([subnet]),
          subnet: () => Promise.resolve(detail),
          ipHistory,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /192\.168\.3\.3: Sin identificar/ }));
    const history = await screen.findByRole('region', { name: 'Historial de 192.168.3.3' });
    expect(await within(history).findByText('80:5E:C0:4A:12:9F')).toBeTruthy();
    expect(within(history).getByText('Ahora')).toBeTruthy();
    expect(ipHistory).toHaveBeenCalledWith(3, '192.168.3.3', 30);

    fireEvent.click(within(history).getByRole('button', { name: 'PC-042076' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(42);
    fireEvent.click(within(history).getByRole('button', { name: '90 días' }));
    await waitFor(() => expect(ipHistory).toHaveBeenCalledWith(3, '192.168.3.3', 90));
  });

  it('"Ver en el mapa" elige la sugerida y la hace destellar', async () => {
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({ subnets: () => Promise.resolve([subnet]), subnet: () => Promise.resolve(detail) }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /192\.168\.3\.2: En uso/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Ver en el mapa' }));

    const cell = screen.getByRole('button', { name: '192.168.3.5: Libre' });
    expect(cell.getAttribute('aria-pressed')).toBe('true');
    expect(cell.getAttribute('data-flash')).not.toBeNull();
  });

  it('con un agente viejo en el recolector explica por qué no barre', async () => {
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="ips" />,
      fakePanelApi({
        red: fakeNetworkApi({
          subnets: () => Promise.resolve([subnet]),
          subnet: () => Promise.resolve({ ...detail, subnet: { ...subnet, collectorCanSweep: false } }),
        }),
      }),
    );

    expect(await screen.findByText(/anterior a la versión 0\.3\.4/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Escanear ahora' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('sin subredes explica para qué sirven', async () => {
    renderWithPanel(<RedPage canEdit onOpenEquipo={vi.fn()} initialTab="ips" />, fakePanelApi({}));

    expect(await screen.findByText('Sin subredes')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Agregar subred' })).toBeTruthy();
  });
});

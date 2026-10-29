import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DeviceDetail, PortItem, RackDetail, RoomItem } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { RedPage } from './RedPage';

afterEach(cleanup);

const room: RoomItem = {
  id: 1,
  name: 'Data Center Principal',
  location: 'Cuarto 204',
  branchId: 1,
  branchName: 'Santo Domingo',
  rackCount: 1,
  deviceCount: 2,
};

const rack: RackDetail = {
  rack: {
    id: 5,
    name: 'GAB-A01',
    roomId: 1,
    roomName: room.name,
    branchId: 1,
    branchName: 'Santo Domingo',
    heightUnits: 42,
    usedUnits: 3,
    row: 'A',
    position: 1,
    isActive: true,
    deviceCount: 2,
    qrCode: 'q',
  },
  devices: [
    {
      id: 10,
      name: 'SW-CORE-01',
      type: 'switch',
      modelName: 'Catalyst 9300-48P',
      positionU: 40,
      heightUnits: 1,
      status: 'en_linea',
      portCount: 52,
      portsConnected: 3,
      portsWithProblems: 0,
    },
    {
      id: 11,
      name: 'PP-A01-01',
      type: 'patch_panel',
      modelName: 'CP48BLY',
      positionU: 35,
      heightUnits: 2,
      status: 'pasivo',
      portCount: 48,
      portsConnected: 1,
      portsWithProblems: 0,
    },
  ],
};

const port = (overrides: Partial<PortItem>): PortItem => ({
  id: 100,
  number: 1,
  name: 'Gi1/0/1',
  type: 'rj45',
  vlan: 20,
  speedMbps: 1000,
  status: 'libre',
  isTrunk: false,
  isVmHost: false,
  isManualAssignment: false,
  cableId: null,
  cableTo: null,
  machineId: null,
  machineName: null,
  ...overrides,
});

const device: DeviceDetail = {
  id: 10,
  name: 'SW-ACC-02',
  type: 'switch',
  modelId: 2,
  brand: 'Cisco',
  modelName: 'Catalyst 9200-48P',
  isActiveDevice: true,
  branchId: 1,
  branchName: 'Santo Domingo',
  rackId: 5,
  rackName: 'GAB-A01',
  roomId: 1,
  roomName: room.name,
  positionU: 38,
  heightUnits: 1,
  managementIp: '10.10.0.2',
  firmware: null,
  status: 'advertencia',
  lastPolledAt: null,
  snmpUser: 'monitor',
  hasSnmpCredential: true,
  qrCode: 'q',
  createdAt: new Date().toISOString(),
  ports: [
    port({
      id: 112,
      number: 12,
      name: 'Gi1/0/12',
      status: 'conectado',
      cableId: 7,
      cableTo: 'PP-A01-01 P12',
      machineId: 3,
      machineName: 'CONTA-03',
    }),
    port({ id: 115, number: 15, name: 'Gi1/0/15', status: 'errores' }),
    port({ id: 149, number: 49, name: 'Te1/1/1', type: 'sfp_plus', status: 'conectado', isTrunk: true }),
  ],
};

describe('Red y data center (Fase 4.1)', () => {
  it('dibuja el gabinete y mueve un equipo arrastrándolo a otra U', async () => {
    const placeDevice = vi.fn(() => Promise.resolve({ id: 10 }));
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        red: fakeNetworkApi({
          rooms: () => Promise.resolve([room]),
          racks: () => Promise.resolve([rack.rack]),
          rack: () => Promise.resolve(rack),
          placeDevice,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /GAB-A01/ }));
    const drawing = await screen.findByRole('list', { name: 'Gabinete GAB-A01' });
    expect(within(drawing).getByText('SW-CORE-01')).toBeTruthy();

    const data = new Map<string, string>();
    const dataTransfer = {
      setData: (k: string, v: string) => data.set(k, v),
      getData: (k: string) => data.get(k) ?? '',
    };
    fireEvent.dragStart(within(drawing).getByTitle(/SW-CORE-01/), { dataTransfer });
    const slot = within(drawing).getByLabelText('Agregar equipo en la U 20').parentElement!;
    fireEvent.dragOver(slot, { dataTransfer });
    fireEvent.drop(slot, { dataTransfer });

    await waitFor(() => expect(placeDevice).toHaveBeenCalledWith(10, 5, 20));
  });

  it('el switch muestra sus puertos por color y el panel del puerto con su equipo', async () => {
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={onOpenEquipo} />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () =>
            Promise.resolve([
              {
                id: 10,
                name: 'SW-ACC-02',
                type: 'switch',
                brand: 'Cisco',
                modelName: 'Catalyst 9200-48P',
                branchId: 1,
                branchName: 'Santo Domingo',
                rackId: 5,
                rackName: 'GAB-A01',
                roomName: room.name,
                positionU: 38,
                heightUnits: 1,
                managementIp: '10.10.0.2',
                status: 'advertencia',
                lastPolledAt: null,
                portCount: 3,
                portsConnected: 2,
                portsWithProblems: 1,
              },
            ]),
          device: () => Promise.resolve(device),
        }),
      }),
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Dispositivos' }));
    fireEvent.click(await screen.findByRole('button', { name: 'SW-ACC-02' }));

    const trunk = await screen.findByRole('listitem', { name: 'Puerto Te1/1/1: troncal' });
    expect(trunk.getAttribute('data-port')).toBe('troncal');
    expect(screen.getByRole('listitem', { name: 'Puerto Gi1/0/15: Con errores' }).getAttribute('data-port')).toBe(
      'errores',
    );

    fireEvent.click(screen.getByRole('listitem', { name: 'Puerto Gi1/0/12: Conectado' }));
    expect(screen.getByText('PP-A01-01 P12')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'CONTA-03' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(3);
    expect(screen.queryByRole('button', { name: 'Conectar cable' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Quitar cable' })).toBeTruthy();
  });

  it('la ruta de una roseta muestra cada tramo hasta el core', async () => {
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        red: fakeNetworkApi({
          outlets: () =>
            Promise.resolve([
              {
                id: 3,
                code: 'P-2-15',
                location: 'Contabilidad',
                branchId: 1,
                branchName: 'Santo Domingo',
                cableId: 9,
                portId: 112,
                portLabel: 'PP-A01-01 P12',
                qrCode: 'q',
              },
            ]),
          path: () =>
            Promise.resolve([
              { kind: 'roseta', deviceId: null, name: 'P-2-15', detail: 'Roseta', cableLabel: null },
              { kind: 'patch_panel', deviceId: 11, name: 'PP-A01-01 P12', detail: 'Patch panel', cableLabel: 'P-2-15' },
              {
                kind: 'switch',
                deviceId: 10,
                name: 'SW-ACC-02 Gi1/0/12',
                detail: 'switch',
                cableLabel: 'PP12-ACC02-12',
              },
              { kind: 'uplink', deviceId: 10, name: 'SW-ACC-02 Te1/1/1', detail: 'Uplink', cableLabel: null },
              { kind: 'switch', deviceId: 1, name: 'SW-CORE-01 Te1/1/1', detail: 'switch', cableLabel: 'LLDP' },
            ]),
        }),
      }),
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Rosetas' }));
    expect(screen.queryByRole('button', { name: 'Agregar roseta' })).toBeNull();
    fireEvent.click(await screen.findByRole('button', { name: 'Ruta' }));

    expect(await screen.findByText('SW-CORE-01 Te1/1/1')).toBeTruthy();
    expect(screen.getByText(/cable PP12-ACC02-12/)).toBeTruthy();
  });
});

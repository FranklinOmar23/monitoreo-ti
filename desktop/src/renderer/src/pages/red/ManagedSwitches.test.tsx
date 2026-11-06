import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DeviceDetail, DeviceListItem } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { RedPage } from './RedPage';

afterEach(cleanup);

const now = new Date().toISOString();

const base: DeviceDetail = {
  id: 8,
  name: 'FGT-SD-02',
  type: 'firewall',
  modelId: 3,
  brand: 'Fortinet',
  modelName: 'FortiGate 200G',
  isActiveDevice: true,
  branchId: 1,
  branchName: 'Santo Domingo',
  rackId: null,
  rackName: null,
  roomId: null,
  roomName: null,
  positionU: null,
  heightUnits: 1,
  managementIp: '192.168.4.3',
  firmware: null,
  status: 'en_linea',
  lastPolledAt: now,
  snmpUser: 'Centinela',
  hasSnmpCredential: true,
  qrCode: 'q',
  createdAt: now,
  collectorId: 8,
  collectorName: 'PC-042076',
  lastError: null,
  ports: [],
  apiUrl: 'https://192.168.4.3:12443',
  hasApiToken: true,
  apiAcceptSelfSigned: true,
  apiLastOkAt: now,
  apiLastError: null,
};

const item = (id: number, name: string, type: string) =>
  ({
    id,
    name,
    type,
    brand: 'Fortinet',
    modelName: '',
    branchId: 1,
    branchName: 'Santo Domingo',
    rackId: null,
    rackName: null,
    roomName: null,
    positionU: null,
    heightUnits: 1,
    managementIp: null,
    status: 'en_linea',
    lastPolledAt: now,
    portCount: 0,
    portsConnected: 0,
    portsWithProblems: 0,
  }) as unknown as DeviceListItem;

describe('FortiSwitch por la API del FortiGate (decisión 80)', () => {
  it('detecta los switches, propone los que faltan y los registra con el nombre elegido', async () => {
    const detectSwitches = vi.fn(() =>
      Promise.resolve({
        ok: true,
        error: null,
        fields: ['serial', 'ports'],
        switches: [
          {
            serial: 'S248EPTF18000001',
            name: 'SW-PISO1',
            model: 'S248EP',
            ip: '10.255.1.2',
            connected: true,
            portCount: 52,
            firmware: 'v7.2.5',
            deviceId: null,
            deviceName: null,
          },
          {
            serial: 'S124EF0000000002',
            name: 'SW-PISO2',
            model: 'S124EF',
            ip: '10.255.1.3',
            connected: true,
            portCount: 28,
            firmware: null,
            deviceId: 41,
            deviceName: 'SW-PISO2',
          },
        ],
      }),
    );
    const registerSwitches = vi.fn(() => Promise.resolve({ id: 50 }));
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () => Promise.resolve([item(8, 'FGT-SD-02', 'firewall')]),
          device: () => Promise.resolve(base),
          detectSwitches,
          registerSwitches,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'FGT-SD-02' }));
    const card = (await screen.findByRole('heading', { name: 'Switches administrados' })).closest('section')!;
    fireEvent.click(within(card).getByRole('button', { name: 'Detectar' }));

    // El que ya está en Centinela no se ofrece; el nuevo viene marcado con su nombre.
    expect(await within(card).findByText('En Centinela')).toBeTruthy();
    expect(within(card).queryByRole('checkbox', { name: 'Registrar SW-PISO2' })).toBeNull();
    expect((within(card).getByRole('checkbox', { name: 'Registrar SW-PISO1' }) as HTMLInputElement).checked).toBe(true);
    fireEvent.change(within(card).getByLabelText('Nombre para S248EPTF18000001'), {
      target: { value: 'SW-RECEPCION' },
    });
    fireEvent.click(within(card).getByRole('button', { name: /Registrar 1 switch en Santo Domingo/ }));

    await waitFor(() =>
      expect(registerSwitches).toHaveBeenCalledWith(8, {
        switches: [
          { serie: 'S248EPTF18000001', nombre: 'SW-RECEPCION', modelo: 'S248EP', puertos: 52, ip: '10.255.1.2' },
        ],
      }),
    );
  });

  it('si el FortiGate no deja leer los switches, lo dice', async () => {
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () => Promise.resolve([item(8, 'FGT-SD-02', 'firewall')]),
          device: () => Promise.resolve(base),
          detectSwitches: () =>
            Promise.resolve({ ok: false, error: 'Sin permiso (403): el perfil…', switches: [], fields: [] }),
        }),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'FGT-SD-02' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Detectar' }));
    expect((await screen.findByRole('alert')).textContent).toContain('403');
  });

  it('la ficha del switch dice que sus datos vienen del FortiGate', async () => {
    const sw: DeviceDetail = {
      ...base,
      id: 41,
      name: 'SW-PISO1',
      type: 'switch',
      modelName: 'FortiSwitch S248EP',
      managementIp: '10.255.1.2',
      snmpUser: null,
      hasSnmpCredential: false,
      apiUrl: null,
      hasApiToken: false,
      fortiGateId: 8,
      fortiGateName: 'FGT-SD-02',
      serialNumber: 'S248EPTF18000001',
    };
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () => Promise.resolve([item(41, 'SW-PISO1', 'switch')]),
          device: () => Promise.resolve(sw),
        }),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'SW-PISO1' }));
    expect(await screen.findByText('Por la API de FGT-SD-02')).toBeTruthy();
    expect(screen.getByText('S248EPTF18000001')).toBeTruthy();
    expect(screen.queryByRole('heading', { name: 'Switches administrados' })).toBeNull();
  });
});

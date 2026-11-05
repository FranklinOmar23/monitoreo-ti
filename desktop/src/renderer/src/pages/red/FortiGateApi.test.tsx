import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DeviceDetail, DeviceListItem } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { RedPage } from './RedPage';

afterEach(cleanup);

const now = new Date().toISOString();

const firewall: DeviceDetail = {
  id: 8,
  name: 'FGT-SD-01',
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
  managementIp: '192.168.3.3',
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
  apiUrl: null,
  hasApiToken: false,
  apiAcceptSelfSigned: true,
  apiLastOkAt: null,
  apiLastError: null,
};

const listItem = {
  id: 8,
  name: 'FGT-SD-01',
  type: 'firewall',
  brand: 'Fortinet',
  modelName: 'FortiGate 200G',
  branchId: 1,
  branchName: 'Santo Domingo',
  rackId: null,
  rackName: null,
  roomName: null,
  positionU: null,
  heightUnits: 1,
  managementIp: '192.168.3.3',
  status: 'en_linea',
  lastPolledAt: now,
  portCount: 0,
  portsConnected: 0,
  portsWithProblems: 0,
} as unknown as DeviceListItem;

function renderDevice(detail: DeviceDetail, extra: Parameters<typeof fakeNetworkApi>[0] = {}) {
  renderWithPanel(
    <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
    fakePanelApi({
      red: fakeNetworkApi({
        devices: () => Promise.resolve([listItem]),
        device: () => Promise.resolve(detail),
        models: () => Promise.resolve([]),
        racks: () => Promise.resolve([]),
        collectors: () => Promise.resolve([]),
        ...extra,
      }),
    }),
  );
}

describe('API REST del FortiGate (decisión 76)', () => {
  it('sin API la ficha dice que va solo por SNMP y no ofrece probar', async () => {
    renderDevice(firewall);
    fireEvent.click(await screen.findByRole('button', { name: 'FGT-SD-01' }));
    expect(await screen.findByText('Sin configurar (solo SNMP)')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Probar API' })).toBeNull();
  });

  it('en el formulario se pega el token y "Probar API" lo prueba sin guardar', async () => {
    const testDeviceApi = vi.fn(() =>
      Promise.resolve({
        ok: true,
        error: null,
        model: 'FortiGate 200G',
        version: 'v7.4.4 build 2662',
        hostname: 'FGT-SD-01',
        serial: 'FG200G0001',
        milliseconds: 42,
      }),
    );
    const saveDevice = vi.fn(() => Promise.resolve({ id: 8 }));
    renderDevice(firewall, { testDeviceApi, saveDevice });

    fireEvent.click(await screen.findByRole('button', { name: 'FGT-SD-01' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Editar' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Usar https://192.168.3.3:12443' }));
    fireEvent.change(within(dialog).getByLabelText('Token del usuario API'), {
      target: { value: 'h7k2m9p4q1w8e5r3t6y0u2i4o7p1as' },
    });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Probar API' }));

    await waitFor(() =>
      expect(testDeviceApi).toHaveBeenCalledWith(8, {
        apiUrl: 'https://192.168.3.3:12443',
        apiToken: 'h7k2m9p4q1w8e5r3t6y0u2i4o7p1as',
        apiAceptarCertificado: true,
      }),
    );
    expect((await within(dialog).findByRole('status')).textContent).toContain('FortiGate 200G');
    expect(saveDevice).not.toHaveBeenCalled();

    fireEvent.submit(dialog.querySelector('form')!);
    await waitFor(() =>
      expect(saveDevice).toHaveBeenCalledWith(
        8,
        expect.objectContaining({
          apiUrl: 'https://192.168.3.3:12443',
          apiToken: 'h7k2m9p4q1w8e5r3t6y0u2i4o7p1as',
        }),
      ),
    );
  });

  it('con token guardado, la ficha muestra el estado y prueba lo guardado; un 401 se explica', async () => {
    const testDeviceApi = vi.fn(() =>
      Promise.resolve({
        ok: false,
        error: 'El FortiGate rechazó el token (401): revise "Trusted Hosts".',
        model: null,
        version: null,
        hostname: null,
        serial: null,
        milliseconds: 12,
      }),
    );
    renderDevice(
      { ...firewall, apiUrl: 'https://192.168.3.3:12443', hasApiToken: true, apiLastOkAt: now },
      { testDeviceApi },
    );

    fireEvent.click(await screen.findByRole('button', { name: 'FGT-SD-01' }));
    expect(await screen.findByText(/https:\/\/192\.168\.3\.3:12443 · leída/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Probar API' }));
    await waitFor(() =>
      expect(testDeviceApi).toHaveBeenCalledWith(8, { apiUrl: null, apiToken: null, apiAceptarCertificado: true }),
    );
    expect((await screen.findByRole('status')).textContent).toContain('Trusted Hosts');
  });
});

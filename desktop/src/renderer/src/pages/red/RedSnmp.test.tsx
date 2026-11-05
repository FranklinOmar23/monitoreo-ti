import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DeviceDetail, NetworkSummary } from '@monitoreoti/shared';
import {
  alertItem,
  alertList,
  fakeNetworkApi,
  fakePanelApi,
  machine,
  machineDetail,
  machineList,
  renderWithPanel,
} from '../../test/fakes';
import { FichaTabs } from '../FichaTabs';
import { RedPage } from './RedPage';

afterEach(cleanup);

const now = new Date().toISOString();

const summary: NetworkSummary = {
  devices: 6,
  online: 4,
  warning: 0,
  critical: 1,
  notResponding: 1,
  withoutCollector: 1,
  ports: 200,
  portsConnected: 120,
  portsWithProblems: 2,
  trunks: 6,
  unknownDevices: 3,
  differences: 1,
  openAlerts: 2,
  collectors: 2,
  collectorsOnline: 1,
  attention: [
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
      roomName: 'Data Center',
      positionU: 38,
      heightUnits: 1,
      managementIp: '10.10.0.2',
      status: 'critico',
      lastPolledAt: now,
      portCount: 52,
      portsConnected: 30,
      portsWithProblems: 2,
    },
  ],
};

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
  roomName: 'Data Center',
  positionU: 38,
  heightUnits: 1,
  managementIp: '10.10.0.2',
  firmware: '17.9.4',
  status: 'sin_respuesta',
  lastPolledAt: null,
  snmpUser: 'monitor',
  hasSnmpCredential: true,
  qrCode: 'q',
  createdAt: now,
  collectorId: 4,
  collectorName: 'CONTA-01',
  lastError: 'SNMP rechazó la consulta: usuario desconocido',
  ports: [
    {
      id: 105,
      number: 5,
      name: 'Gi1/0/5',
      type: 'rj45',
      vlan: 20,
      speedMbps: null,
      status: 'libre',
      isTrunk: false,
      isVmHost: false,
      isManualAssignment: false,
      cableId: null,
      cableTo: null,
      machineId: null,
      machineName: null,
    },
  ],
};

describe('Recolector SNMP y correlación (Fase 4.2 y 4.3)', () => {
  it('el resumen de red muestra los indicadores, lo que necesita atención y las alertas de red', async () => {
    const onOpenAlerta = vi.fn();
    const alerts = vi.fn(() =>
      Promise.resolve(
        alertList([
          alertItem({
            id: 77,
            machineId: null,
            machineName: null,
            deviceId: 10,
            deviceName: 'SW-ACC-02',
            message: 'Se cayó el puerto de CONTA-03',
          }),
        ]),
      ),
    );
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} onOpenAlerta={onOpenAlerta} />,
      fakePanelApi({ alerts, red: fakeNetworkApi({ summary: () => Promise.resolve(summary) }) }),
    );

    expect(await screen.findByText('SW-ACC-02')).toBeTruthy();
    expect(screen.getByText('Sin responder SNMP')).toBeTruthy();
    expect(screen.getByText('1 sin recolector asignado')).toBeTruthy();
    fireEvent.click(await screen.findByRole('button', { name: 'Se cayó el puerto de CONTA-03' }));
    expect(onOpenAlerta).toHaveBeenCalledWith(77);
    expect(alerts).toHaveBeenCalledWith(expect.objectContaining({ red: true }));
  });

  it('desconocidos agrupa las MAC aleatorias y diferencias explica qué no coincide', async () => {
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} initialTab="desconocidos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          unknown: () =>
            Promise.resolve([
              {
                mac: '3C:52:82:00:00:99',
                vendor: 'Hewlett Packard',
                ip: '10.10.20.50',
                vlan: 20,
                deviceId: 10,
                deviceName: 'SW-ACC-02',
                portId: 105,
                portLabel: 'SW-ACC-02 Gi1/0/5',
                isRandom: false,
                count: 1,
                firstSeenAt: now,
                lastSeenAt: now,
                viaAccessPoint: false,
              },
              {
                mac: 'DA:A1:19:00:00:01',
                vendor: null,
                ip: null,
                vlan: 30,
                deviceId: 12,
                deviceName: 'AP-PISO2',
                portId: 201,
                portLabel: 'AP-PISO2 1',
                isRandom: true,
                count: 4,
                firstSeenAt: now,
                lastSeenAt: now,
                viaAccessPoint: true,
              },
            ]),
          differences: () =>
            Promise.resolve([
              {
                kind: 'equipo_sin_cable',
                title: 'Equipo en un puerto sin cable documentado',
                detail: 'CONTA-03 está conectado a SW-ACC-02 Gi1/0/5, pero ese puerto no tiene cable documentado.',
                deviceId: 10,
                portId: 105,
                machineId: 3,
              },
            ]),
        }),
      }),
    );

    expect(await screen.findByText('Hewlett Packard')).toBeTruthy();
    expect(screen.getByText('4 MAC aleatorias')).toBeTruthy();
    expect(screen.getByText('Vía access point (Wi-Fi)')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: 'Diferencias' }));
    expect(await screen.findByText(/CONTA-03 está conectado a SW-ACC-02 Gi1\/0\/5/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Ver equipo' })).toBeTruthy();
  });

  it('el dispositivo dice por qué falló el sondeo y un puerto se asigna a mano', async () => {
    const assignPort = vi.fn(() => Promise.resolve({ id: 105 }));
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        machines: () =>
          Promise.resolve(machineList([machine({ id: 3, name: 'IMPRESORA-02', ipAddress: '10.10.20.90' })])),
        red: fakeNetworkApi({
          devices: () => Promise.resolve(summary.attention),
          device: () => Promise.resolve(device),
          assignPort,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'SW-ACC-02' }));
    expect((await screen.findByRole('alert')).textContent).toContain('usuario desconocido');
    expect(screen.getByText('CONTA-01').closest('li')?.textContent).toContain('Lo consulta CONTA-01');
    // El usuario SNMP rechazado se muestra con su código.
    expect(screen.getByText('ERR_SNMP_AUTH')).toBeTruthy();

    fireEvent.click(screen.getByRole('listitem', { name: 'Puerto Gi1/0/5: Libre' }));
    fireEvent.click(screen.getByRole('button', { name: 'Asignar a mano' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByLabelText('Un equipo en particular'));
    await within(dialog).findByRole('option', { name: /IMPRESORA-02/ });
    fireEvent.change(within(dialog).getByLabelText('Equipo *'), { target: { value: '3' } });
    fireEvent.submit(dialog.querySelector('form')!);

    await waitFor(() => expect(assignPort).toHaveBeenCalledWith(105, { maquinaId: 3, troncal: null }));
  });

  it('"Sondear ahora" pide el sondeo al recolector y espera el resultado', async () => {
    const pollDevice = vi.fn(() => Promise.resolve({ id: 10 }));
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () => Promise.resolve(summary.attention),
          device: () => Promise.resolve(device),
          pollDevice,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'SW-ACC-02' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sondear ahora' }));
    await waitFor(() => expect(pollDevice).toHaveBeenCalledWith(10));
    // Mientras no cambie el resultado del sondeo, el botón espera.
    expect(await screen.findByRole('button', { name: 'Sondeando…' })).toBeTruthy();
  });

  it('si el recolector no está conectado lo dice', async () => {
    const pollDevice = vi.fn(() =>
      Promise.reject(new Error('El recolector CONTA-01 no está conectado; se sondeará cuando vuelva.')),
    );
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () => Promise.resolve(summary.attention),
          device: () => Promise.resolve({ ...device, lastError: null }),
          pollDevice,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'SW-ACC-02' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Sondear ahora' }));
    expect((await screen.findByRole('alert')).textContent).toContain('no está conectado');
    expect(screen.getByRole('button', { name: 'Sondear ahora' })).toBeTruthy();
  });

  it('sin permiso de edición no hay botón para sondear', async () => {
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} initialTab="dispositivos" />,
      fakePanelApi({
        red: fakeNetworkApi({
          devices: () => Promise.resolve(summary.attention),
          device: () => Promise.resolve(device),
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'SW-ACC-02' }));
    await screen.findByRole('heading', { name: 'SW-ACC-02' });
    expect(screen.queryByRole('button', { name: 'Sondear ahora' })).toBeNull();
  });

  it('la ficha muestra el switch, el puerto y la VLAN del equipo', async () => {
    renderWithPanel(
      <FichaTabs tab="red" machine={machineDetail()} />,
      fakePanelApi({
        networkConnection: () =>
          Promise.resolve({
            deviceId: 10,
            switchName: 'SW-ACC-02',
            portId: 112,
            portLabel: 'Gi1/0/12',
            vlan: 20,
            rackName: 'GAB-A01',
            roomName: 'Data Center',
            ipAddress: '10.10.20.31',
            macAddress: '3C:52:82:A1:00:03',
            lastSeenAt: now,
            viaAccessPoint: false,
            isManual: false,
          }),
      }),
    );

    expect(await screen.findByText('SW-ACC-02')).toBeTruthy();
    expect(screen.getByText('Gi1/0/12')).toBeTruthy();
    expect(screen.getByText('20')).toBeTruthy();
  });
});

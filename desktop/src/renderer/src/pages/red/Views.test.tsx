import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DeviceListItem, RackDevice, RackItem, RoomItem, TopologyEndpoint } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { firstFreeU } from './DataCenterView';
import { RedPage } from './RedPage';
import {
  cellAt,
  deviceBoxes,
  layoutRoom,
  moveRack,
  nextRowName,
  planRows,
  ROW_PITCH,
  SLOT,
  U_METERS,
  worstStatus,
} from './scene3d';
import { layoutTopology } from './TopologyMap';

afterEach(cleanup);

const rack = (id: number, name: string, row: string | null, position: number | null): RackItem => ({
  id,
  name,
  roomId: 1,
  roomName: 'Data Center',
  branchId: 1,
  branchName: 'Santo Domingo',
  heightUnits: 42,
  usedUnits: 10,
  row,
  position,
  isActive: true,
  deviceCount: 3,
  qrCode: 'q',
});

const device = (id: number, positionU: number, heightUnits: number, status: RackDevice['status']): RackDevice => ({
  id,
  name: `EQ-${id}`,
  type: 'switch',
  modelName: 'X',
  positionU,
  heightUnits,
  status,
  portCount: 0,
  portsConnected: 0,
  portsWithProblems: 0,
});

describe('plano y 3D (W-43, W-44, W-59)', () => {
  it('ordena los gabinetes por fila y posición, con huecos y los sin fila al final', () => {
    const rows = planRows([
      rack(1, 'B-02', 'b', 2),
      rack(2, 'A-03', 'A', 3),
      rack(3, 'A-01', 'A', 1),
      rack(4, 'SUELTO', null, null),
    ]);
    expect(rows.map((r) => r.row)).toEqual(['A', 'B', null]);
    expect(rows[0]!.racks.map((r) => [r.rack.name, r.column])).toEqual([
      ['A-01', 1],
      ['A-03', 3],
    ]);
    const placed = layoutRoom([rack(3, 'A-01', 'A', 1), rack(1, 'B-02', 'B', 2)]);
    expect(placed[1]!.z).toBeGreaterThan(placed[0]!.z);
    expect(placed[0]!.height).toBeCloseTo(42 * U_METERS);
  });

  it('las filas se miran de a pares y el piso del 3D se lee como fila y posición (decisión 96)', () => {
    const racks = [rack(1, 'A-01', 'A', 1), rack(2, 'B-01', 'B', 1), rack(3, 'C-01', 'C', 1)];
    expect(layoutRoom(racks).map((p) => p.rotation)).toEqual([0, Math.PI, 0]);
    expect(cellAt(racks, SLOT * 2 + 0.1, ROW_PITCH * 1.1)).toEqual({ row: 'B', position: 3 });
    // Más allá de la última fila: una fila nueva.
    expect(cellAt(racks, 0, ROW_PITCH * 5)).toEqual({ row: 'D', position: 1 });
    expect(nextRowName(['A', 'B'])).toBe('C');
    expect(nextRowName(['1', '2'])).toBe('3');
    expect(nextRowName([])).toBe('A');
  });

  it('mover un gabinete a un lugar libre, intercambiar con otro o sacarlo del plano', () => {
    // A-02 no tiene posición guardada: se ve en la 2 y, al tocar la fila, se guarda.
    const racks = [
      rack(1, 'R1', 'A', 1),
      rack(2, 'R2', 'A', null),
      rack(3, 'R3', 'B', 1),
      rack(4, 'SUELTO', null, null),
    ];
    expect(moveRack(racks, 3, { row: 'A', position: 4 })).toEqual([
      { gabineteId: 2, fila: 'A', posicion: 2 },
      { gabineteId: 3, fila: 'A', posicion: 4 },
    ]);
    // Sobre otro: se intercambian.
    expect(moveRack(racks, 1, { row: 'B', position: 1 })).toEqual([
      { gabineteId: 1, fila: 'B', posicion: 1 },
      { gabineteId: 2, fila: 'A', posicion: 2 },
      { gabineteId: 3, fila: 'A', posicion: 1 },
    ]);
    // Desde «Sin ubicar» a un lugar libre sí; sobre otro, no.
    expect(moveRack(racks, 4, { row: 'C', position: 1 })).toEqual([{ gabineteId: 4, fila: 'C', posicion: 1 }]);
    expect(moveRack(racks, 4, { row: 'A', position: 1 })).toBeNull();
    expect(moveRack(racks, 1, null)).toEqual([
      { gabineteId: 1, fila: null, posicion: null },
      { gabineteId: 2, fila: 'A', posicion: 2 },
    ]);
    // Al mismo lugar: nada que guardar.
    expect(moveRack(racks, 1, { row: 'A', position: 1 })).toEqual([]);
  });

  it('cada equipo va a su altura real y el gabinete toma el color de su peor equipo', () => {
    const [box] = deviceBoxes([device(1, 1, 2, 'en_linea')]);
    expect(box!.y).toBeCloseTo(U_METERS);
    expect(worstStatus([device(1, 1, 1, 'en_linea'), device(2, 5, 1, 'critico')])).toBe('critico');
  });

  it('la vista 3D sin WebGL avisa y deja usar el plano', async () => {
    const room: RoomItem = {
      id: 1,
      name: 'Data Center',
      location: null,
      branchId: 1,
      branchName: 'Santo Domingo',
      rackCount: 2,
      deviceCount: 2,
    };
    const onOpen = vi.fn();
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} initialTab="datacenter" />,
      fakePanelApi({
        red: fakeNetworkApi({
          rooms: () => Promise.resolve([room]),
          racks: () => Promise.resolve([rack(1, 'GAB-A01', 'A', 1), rack(2, 'GAB-A02', 'A', 2)]),
          rack: () => Promise.resolve({ rack: rack(1, 'GAB-A01', 'A', 1), devices: [] }),
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Plano' }));
    expect(screen.getByRole('list', { name: 'Plano de la sala' }).textContent).toContain('Fila A');

    fireEvent.click(screen.getByRole('button', { name: '3D' }));
    // three.js se carga aparte: con todas las pruebas en paralelo puede tardar más de un segundo.
    expect(await screen.findByText(/no tiene aceleración gráfica/, undefined, { timeout: 5000 })).toBeTruthy();
    expect(onOpen).not.toHaveBeenCalled();
  });
});

describe('editar la sala (decisión 96)', () => {
  const room: RoomItem = {
    id: 1,
    name: 'Data Center',
    location: null,
    branchId: 1,
    branchName: 'Santo Domingo',
    rackCount: 2,
    deviceCount: 0,
  };
  const loose: DeviceListItem = {
    id: 12,
    name: 'S124FFTF24050124',
    type: 'switch',
    brand: 'Fortinet',
    modelName: 'FortiSwitch 124F',
    branchId: 1,
    branchName: 'Santo Domingo',
    rackId: null,
    rackName: null,
    roomName: null,
    positionU: null,
    heightUnits: 1,
    managementIp: '10.255.1.2',
    status: 'en_linea',
    lastPolledAt: null,
    portCount: 24,
    portsConnected: 10,
    portsWithProblems: 0,
  };

  it('la primera U libre donde entra un equipo', () => {
    expect(firstFreeU(42, [], 1)).toBe(1);
    expect(firstFreeU(42, [device(1, 1, 2, 'en_linea')], 1)).toBe(3);
    expect(firstFreeU(4, [device(1, 2, 1, 'en_linea')], 2)).toBe(3);
    expect(firstFreeU(2, [device(1, 1, 2, 'en_linea')], 1)).toBeNull();
  });

  it('un switch que trajo el FortiGate va al gabinete desde «Sin gabinete»', async () => {
    const placeDevice = vi.fn(() => Promise.resolve({ id: 12 }));
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="datacenter" />,
      fakePanelApi({
        red: fakeNetworkApi({
          rooms: () => Promise.resolve([room]),
          racks: () => Promise.resolve([rack(1, 'GAB-A01', 'A', 1)]),
          rack: () => Promise.resolve({ rack: rack(1, 'GAB-A01', 'A', 1), devices: [device(1, 1, 2, 'en_linea')] }),
          devices: () => Promise.resolve([loose, { ...loose, id: 13, name: 'EN-OTRO', rackId: 9 }]),
          placeDevice,
        }),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /GAB-A01/ }));
    const list = await screen.findByRole('list', { name: 'Equipos sin gabinete' });
    expect(within(list).getByText('S124FFTF24050124')).toBeTruthy();
    expect(within(list).queryByText('EN-OTRO')).toBeNull();

    fireEvent.click(within(list).getByRole('button', { name: 'Poner en U3' }));
    await waitFor(() => expect(placeDevice).toHaveBeenCalledWith(12, 1, 3));
  });

  it('en el plano, soltar un gabinete en otro lugar guarda el plano', async () => {
    const saveRoomLayout = vi.fn(() => Promise.resolve({ id: 1 }));
    renderWithPanel(
      <RedPage canEdit onOpenEquipo={vi.fn()} initialTab="datacenter" />,
      fakePanelApi({
        red: fakeNetworkApi({
          rooms: () => Promise.resolve([room]),
          racks: () => Promise.resolve([rack(1, 'GAB-A01', 'A', 1), rack(2, 'GAB-SUELTO', null, null)]),
          saveRoomLayout,
        }),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Plano' }));
    fireEvent.click(screen.getByRole('button', { name: 'Editar plano' }));

    const data = new Map<string, string>();
    const dataTransfer = {
      setData: (k: string, v: string) => data.set(k, v),
      getData: (k: string) => data.get(k) ?? '',
      effectAllowed: 'move',
    };
    fireEvent.dragStart(screen.getByRole('button', { name: /GAB-SUELTO/ }), { dataTransfer });
    fireEvent.drop(screen.getByLabelText('Fila A, posición 2: libre'), { dataTransfer });
    await waitFor(() => expect(saveRoomLayout).toHaveBeenCalledWith(1, [{ gabineteId: 2, fila: 'A', posicion: 2 }]));

    fireEvent.click(screen.getByRole('button', { name: /Agregar fila/ }));
    expect(screen.getByText('Fila B')).toBeTruthy();
  });
});

describe('topología (W-51)', () => {
  it('pone el core arriba y los switches de acceso debajo; los sueltos al final', () => {
    const node = (id: number, name: string, trunks: number) => ({
      id,
      name,
      type: 'switch' as const,
      status: 'en_linea' as const,
      branchName: 'SD',
      trunks,
    });
    const { nodes } = layoutTopology({
      nodes: [node(1, 'SW-ACC-01', 1), node(2, 'SW-CORE-01', 4), node(3, 'SW-ACC-02', 1), node(4, 'AP-SUELTO', 0)],
      links: [
        { sourceId: 1, targetId: 2, sourcePort: 'Te1/1/1', targetPort: 'Te1/0/1', kind: 'lldp' },
        { sourceId: 3, targetId: 2, sourcePort: 'Te1/1/1', targetPort: 'Te1/0/2', kind: 'cable' },
      ],
    });
    const level = Object.fromEntries(nodes.map((n) => [n.name, n.level]));
    expect(level).toEqual({ 'SW-CORE-01': 0, 'SW-ACC-01': 1, 'SW-ACC-02': 1, 'AP-SUELTO': 2 });
  });

  it('un clic muestra el detalle y el doble clic abre el equipo', async () => {
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} initialTab="topologia" />,
      fakePanelApi({
        red: fakeNetworkApi({
          topology: () =>
            Promise.resolve({
              nodes: [{ id: 2, name: 'SW-CORE-01', type: 'switch', status: 'en_linea', branchName: 'SD', trunks: 4 }],
              links: [],
            }),
          device: () => new Promise(() => undefined),
        }),
      }),
    );

    const node = await screen.findByRole('button', { name: 'SW-CORE-01: En línea' });
    fireEvent.click(node);
    expect(screen.getByRole('button', { name: 'Abrir dispositivo' })).toBeTruthy();
    fireEvent.doubleClick(node);
    // Abre la vista del dispositivo (mientras carga, la ruta muestra su número).
    expect(await screen.findByText('#2')).toBeTruthy();
  });

  it('cuelga los equipos de cada puerto bajo su switch, con filtros y detalle', async () => {
    const onOpenEquipo = vi.fn();
    const endpoint = (portNumber: number, overrides: Partial<TopologyEndpoint>): TopologyEndpoint => ({
      deviceId: 2,
      portId: 100 + portNumber,
      portNumber,
      portLabel: `p${portNumber}`,
      vlan: 10,
      speedMbps: 1000,
      mac: `00:11:22:33:44:${String(portNumber).padStart(2, '0')}`,
      ip: `192.168.3.${portNumber}`,
      vendor: null,
      machineId: null,
      machineName: null,
      machineStatus: null,
      hasAgent: false,
      category: 'desconocido',
      categoryInferred: false,
      kind: 'detectado',
      firstSeenAt: new Date().toISOString(),
      lastSeenAt: new Date().toISOString(),
      ...overrides,
    });
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={onOpenEquipo} initialTab="topologia" />,
      fakePanelApi({
        red: fakeNetworkApi({
          topology: () =>
            Promise.resolve({
              nodes: [
                {
                  id: 2,
                  name: 'SW-SD-01',
                  type: 'switch',
                  status: 'en_linea',
                  branchName: 'SD',
                  trunks: 1,
                  branchId: 1,
                },
              ],
              links: [],
              macTableReadAt: new Date().toISOString(),
              endpoints: [
                endpoint(4, {
                  machineId: 42,
                  machineName: 'PC-042076',
                  category: 'pc',
                  hasAgent: true,
                  machineStatus: 'sano',
                }),
                endpoint(12, { vlan: 30, vendor: 'Yealink', category: 'telefono', categoryInferred: true }),
              ],
            }),
          path: () => Promise.resolve([]),
        }),
      }),
    );

    expect(await screen.findByRole('button', { name: /PC-042076, PC, puerto p4, VLAN 10/ })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Yealink, Teléfono IP, puerto p12, VLAN 30/ }));
    expect(await screen.findByText('Teléfono IP · deducido por el fabricante de la MAC')).toBeTruthy();

    // Filtro de VLAN: solo queda el teléfono.
    fireEvent.change(screen.getByLabelText('VLAN:'), { target: { value: '30' } });
    expect(screen.queryByRole('button', { name: /PC-042076, PC/ })).toBeNull();
    fireEvent.change(screen.getByLabelText('VLAN:'), { target: { value: '' } });

    fireEvent.click(screen.getByRole('button', { name: /PC-042076, PC/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'Abrir ficha' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(42);

    // Solo red: desaparecen los equipos finales.
    fireEvent.click(screen.getByRole('button', { name: 'Solo red' }));
    expect(screen.queryByRole('button', { name: /PC-042076, PC/ })).toBeNull();
  });
});

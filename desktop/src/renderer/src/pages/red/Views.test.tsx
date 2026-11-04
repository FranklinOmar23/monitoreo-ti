import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { RackDevice, RackItem, RoomItem } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { RedPage } from './RedPage';
import { deviceBoxes, layoutRoom, planRows, U_METERS, worstStatus } from './scene3d';
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

  it('un clic en un equipo del mapa lo abre', async () => {
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

    fireEvent.click(await screen.findByRole('button', { name: 'SW-CORE-01: En línea' }));
    // Abre la vista del dispositivo (mientras carga, la ruta muestra su número).
    expect(await screen.findByText('#2')).toBeTruthy();
  });
});

import { cleanup, fireEvent, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TopologyEndpoint, TopologyLink, TopologyNode } from '@monitoreoti/shared';
import { fakeNetworkApi, fakePanelApi, renderWithPanel } from '../../test/fakes';
import { layoutTree, linkKey, pathToRoot } from './TopologyMap';
import { RedPage } from './RedPage';

afterEach(cleanup);

const node = (id: number, name: string, trunks: number, type: TopologyNode['type'] = 'switch'): TopologyNode => ({
  id,
  name,
  type,
  status: 'en_linea',
  branchName: 'SD',
  trunks,
});

const link = (a: number, b: number, pa = 'port1', pb = 'port48'): TopologyLink => ({
  sourceId: a,
  targetId: b,
  sourcePort: pa,
  targetPort: pb,
  kind: 'lldp',
});

const endpoint = (deviceId: number, n: number, overrides: Partial<TopologyEndpoint> = {}): TopologyEndpoint => ({
  deviceId,
  portId: deviceId * 100 + n,
  portNumber: n,
  portLabel: `port${n}`,
  vlan: 4,
  speedMbps: 1000,
  mac: `00:11:22:33:${String(deviceId).padStart(2, '0')}:${String(n).padStart(2, '0')}`,
  ip: `192.168.4.${n}`,
  vendor: null,
  machineId: null,
  machineName: null,
  machineStatus: null,
  hasAgent: false,
  category: 'pc',
  categoryInferred: false,
  kind: 'detectado',
  firstSeenAt: null,
  lastSeenAt: null,
  ...overrides,
});

// FortiGate (core) → dos FortiSwitch; un AP suelto.
const nodes = [
  node(1, 'FGT-SD-02', 2, 'firewall'),
  node(2, 'SW-PISO1', 1),
  node(3, 'SW-PISO2', 1),
  node(9, 'AP-SUELTO', 0, 'access_point'),
];
const links = [link(2, 1, 'port52', 'agregate'), link(3, 1, 'port28', 'agregate')];

describe('topología como árbol (decisión 81)', () => {
  it('cada switch queda bajo su padre y sus equipos debajo de él, en su propia columna', () => {
    const endpoints = [endpoint(2, 1), endpoint(2, 2), endpoint(3, 5)];
    const tree = layoutTree({ nodes, links }, endpoints, new Set());
    const at = Object.fromEntries(tree.nodes.map((n) => [n.name, n]));

    expect(tree.parent.get(2)).toBe(1);
    expect(tree.parent.get(3)).toBe(1);
    expect(at['SW-PISO1']!.y).toBeGreaterThan(at['FGT-SD-02']!.y);
    // El AP sin enlaces va al final, debajo de todo lo demás.
    const lowest = Math.max(...tree.clusters.flatMap((c) => c.endpoints.map((e) => e.y)));
    expect(at['AP-SUELTO']!.y).toBeGreaterThan(lowest);

    // Los equipos de SW-PISO1 quedan debajo de él y no se mezclan con los de SW-PISO2.
    const c1 = tree.clusters.find((c) => c.switchId === 2)!;
    const c2 = tree.clusters.find((c) => c.switchId === 3)!;
    const right1 = Math.max(...c1.endpoints.map((e) => e.x));
    const left2 = Math.min(...c2.endpoints.map((e) => e.x));
    expect(right1).toBeLessThan(left2);
    expect(Math.min(...c1.endpoints.map((e) => e.y))).toBeGreaterThan(at['SW-PISO1']!.y);
  });

  it('muchos equipos en un switch se pliegan en "ver más" sin ensanchar el mapa', () => {
    const endpoints = Array.from({ length: 40 }, (_, i) => endpoint(2, i + 1));
    const tree = layoutTree({ nodes, links }, endpoints, new Set());
    const cluster = tree.clusters.find((c) => c.switchId === 2)!;
    expect(cluster.endpoints).toHaveLength(7);
    expect(cluster.hidden).toBe(33);
    expect(new Set(cluster.endpoints.map((e) => e.x)).size).toBe(2);
  });

  it('la ruta va del equipo hasta el core', () => {
    const tree = layoutTree(
      { nodes: [...nodes, node(4, 'SW-PISO1-B', 0)], links: [...links, link(4, 2)] },
      [],
      new Set(),
    );
    expect(pathToRoot(4, tree.parent)).toEqual([4, 2, 1]);
    expect(linkKey(4, 2)).toBe(linkKey(2, 4));
  });

  it('al elegir un equipo final se ve su ruta hasta el core y lo demás se atenúa', async () => {
    renderWithPanel(
      <RedPage canEdit={false} onOpenEquipo={vi.fn()} initialTab="topologia" />,
      fakePanelApi({
        red: fakeNetworkApi({
          topology: () =>
            Promise.resolve({
              nodes,
              links,
              macTableReadAt: new Date().toISOString(),
              endpoints: [
                endpoint(2, 5, { machineId: 7, machineName: 'PC-07', machineStatus: 'sano', hasAgent: true }),
                endpoint(3, 9),
              ],
            }),
          path: () => Promise.resolve([]),
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /PC-07, PC, puerto port5/ }));
    const route = screen.getByRole('list', { name: 'Ruta hasta el core' });
    expect(
      within(route)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(['PC-07', 'SW-PISO1 · port5', 'FGT-SD-02']);
    const svg = screen.getByRole('group', { name: 'Mapa de topología' });
    expect(svg.getAttribute('data-focus')).toBe('true');
    expect(screen.getByRole('button', { name: 'SW-PISO1: En línea' }).getAttribute('data-path')).toBe('true');
    expect(screen.getByRole('button', { name: 'SW-PISO2: En línea' }).getAttribute('data-path')).toBeNull();

    // Otro clic en el mismo equipo lo suelta.
    fireEvent.click(screen.getByRole('button', { name: /PC-07, PC, puerto port5/ }));
    expect(svg.getAttribute('data-focus')).toBeNull();
    expect(screen.getByRole('button', { name: 'Acercar' })).toBeTruthy();
  });
});

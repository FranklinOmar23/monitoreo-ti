import { useQuery } from '@tanstack/react-query';
import type { Topology, TopologyNode } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { usePanelApi } from '../../data/PanelData';
import { DEVICE_STATUS_LABEL, DEVICE_TYPE_LABEL } from './netLabels';
import { STATUS_COLOR } from './scene3d';

const NODE_W = 150;
const NODE_H = 46;
const GAP_X = 36;
const GAP_Y = 90;

export interface PlacedNode extends TopologyNode {
  x: number;
  y: number;
  level: number;
}

/**
 * Capas del mapa: arriba el equipo con más troncales (el core) y, por enlaces, cada capa siguiente
 * (distribución, acceso). Los equipos sin enlaces van en la última fila.
 */
export function layoutTopology(topology: Topology): { nodes: PlacedNode[]; width: number; height: number } {
  const neighbors = new Map<number, number[]>();
  for (const l of topology.links) {
    neighbors.set(l.sourceId, [...(neighbors.get(l.sourceId) ?? []), l.targetId]);
    neighbors.set(l.targetId, [...(neighbors.get(l.targetId) ?? []), l.sourceId]);
  }
  const level = new Map<number, number>();
  const byImportance = [...topology.nodes].sort(
    (a, b) =>
      b.trunks - a.trunks ||
      (neighbors.get(b.id)?.length ?? 0) - (neighbors.get(a.id)?.length ?? 0) ||
      a.name.localeCompare(b.name),
  );
  // Un recorrido por anchura desde cada componente conectado, empezando por su equipo más central.
  for (const root of byImportance) {
    if (level.has(root.id) || !neighbors.has(root.id)) continue;
    level.set(root.id, 0);
    const queue = [root.id];
    while (queue.length > 0) {
      const id = queue.shift()!;
      for (const next of neighbors.get(id) ?? [])
        if (!level.has(next)) {
          level.set(next, level.get(id)! + 1);
          queue.push(next);
        }
    }
  }
  const deepest = Math.max(-1, ...level.values());
  for (const node of topology.nodes) if (!level.has(node.id)) level.set(node.id, deepest + 1);

  const rows = new Map<number, TopologyNode[]>();
  for (const node of byImportance) rows.set(level.get(node.id)!, [...(rows.get(level.get(node.id)!) ?? []), node]);
  const widest = Math.max(1, ...[...rows.values()].map((r) => r.length));
  const width = widest * (NODE_W + GAP_X) + GAP_X;
  const nodes = [...rows.entries()].flatMap(([lvl, row]) => {
    const rowWidth = row.length * (NODE_W + GAP_X) - GAP_X;
    const start = (width - rowWidth) / 2;
    return row
      .sort((a, b) => a.name.localeCompare(b.name))
      .map((node, i) => ({
        ...node,
        level: lvl,
        x: start + i * (NODE_W + GAP_X),
        y: GAP_Y / 2 + lvl * (NODE_H + GAP_Y),
      }));
  });
  const height = (Math.max(0, ...nodes.map((n) => n.level)) + 1) * (NODE_H + GAP_Y);
  return { nodes, width, height };
}

/** W-51 Mapa de topología: equipos de red y enlaces (sólidos si los ve LLDP, punteados si solo están documentados). */
export function TopologyMap({ onOpenDevice }: { onOpenDevice: (id: number) => void }) {
  const api = usePanelApi();
  const query = useQuery({ queryKey: ['red', 'topologia'], queryFn: () => api.red.topology() });

  if (query.isPending) return <Loading />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  if (query.data.nodes.length === 0) return <EmptyState title="Sin equipos de red activos." />;

  const { nodes, width, height } = layoutTopology(query.data);
  const at = new Map(nodes.map((n) => [n.id, n]));

  return (
    <div className="table-card topology">
      <svg viewBox={`0 0 ${width} ${height}`} width={width} height={height} role="img" aria-label="Mapa de topología">
        {query.data.links.map((l) => {
          const a = at.get(l.sourceId)!;
          const b = at.get(l.targetId)!;
          const [top, bottom] = a.y <= b.y ? [a, b] : [b, a];
          return (
            <g key={`${l.sourceId}-${l.targetId}`} className="topology-link" data-kind={l.kind}>
              <line x1={top.x + NODE_W / 2} y1={top.y + NODE_H} x2={bottom.x + NODE_W / 2} y2={bottom.y} />
              <title>
                {a.name} {l.sourcePort} ↔ {b.name} {l.targetPort} (
                {l.kind === 'lldp' ? 'detectado por LLDP' : 'cableado documentado'})
              </title>
            </g>
          );
        })}
        {nodes.map((n) => (
          <g
            key={n.id}
            className="topology-node"
            transform={`translate(${n.x}, ${n.y})`}
            role="button"
            tabIndex={0}
            aria-label={`${n.name}: ${DEVICE_STATUS_LABEL[n.status]}`}
            onClick={() => onOpenDevice(n.id)}
            onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && onOpenDevice(n.id)}
          >
            <rect width={NODE_W} height={NODE_H} rx={10} />
            <circle cx={14} cy={NODE_H / 2} r={5} fill={STATUS_COLOR[n.status]} />
            <text x={26} y={20} className="mono">
              {n.name}
            </text>
            <text x={26} y={36} className="topology-sub">
              {DEVICE_TYPE_LABEL[n.type]} · {n.branchName}
            </text>
          </g>
        ))}
      </svg>
      <p className="muted note topology-legend">
        Línea continua: enlace detectado por LLDP · punteada: solo documentado en el cableado.
      </p>
    </div>
  );
}

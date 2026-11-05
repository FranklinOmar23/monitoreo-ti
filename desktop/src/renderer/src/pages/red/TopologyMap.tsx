import { useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { EndpointCategory, Topology, TopologyEndpoint, TopologyNode } from '@monitoreoti/shared';
import { Card } from '@components/Card';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { dateTime, timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { DEVICE_STATUS_LABEL, DEVICE_TYPE_LABEL } from './netLabels';

// ------------------------------------------------------------------ medidas del mapa

const NODE_W = 196;
const NODE_H = 58;
const GAP_X = 44;
const GAP_Y = 96;
const EP_W = 172;
const EP_H = 68;
const EP_GAP_X = 22;
const EP_GAP_Y = 18;
/** Columnas de equipos finales bajo un switch, y cuántos se muestran antes de "ver los N". */
const EP_COLS = 4;
const EP_SHOWN = 11;
const CLUSTER_GAP = 56;
const MARGIN = 28;

export interface PlacedNode extends TopologyNode {
  x: number;
  y: number;
  level: number;
}

/**
 * Capas del mapa: arriba el equipo con más troncales (el core) y, por enlaces, cada capa siguiente
 * (distribución, acceso). Los equipos sin enlaces van en la última fila.
 */
export function layoutTopology(topology: Pick<Topology, 'nodes' | 'links'>): {
  nodes: PlacedNode[];
  width: number;
  height: number;
} {
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
        y: MARGIN + lvl * (NODE_H + GAP_Y),
      }));
  });
  const height = (Math.max(0, ...nodes.map((n) => n.level)) + 1) * (NODE_H + GAP_Y);
  return { nodes, width, height };
}

// ------------------------------------------------------------------ equipos finales

export const CATEGORY_LABEL: Record<EndpointCategory, string> = {
  pc: 'PC',
  servidor: 'Servidor',
  impresora: 'Impresora',
  telefono: 'Teléfono IP',
  camara: 'Cámara',
  access_point: 'Access point',
  otro: 'Otro equipo',
  desconocido: 'Desconocido',
};

/** Íconos de trazo (24×24) por clase de equipo. */
const ICONS: Record<EndpointCategory | 'switch' | 'firewall' | 'router' | 'red', string> = {
  pc: 'M3 4h18v12H3z M8 20h8 M12 16v4',
  servidor: 'M4 3h16v6H4z M4 13h16v6H4z M8 6h.01 M8 16h.01',
  impresora: 'M6 9V3h12v6 M6 18H4v-7h16v7h-2 M6 14h12v7H6z',
  telefono: 'M4 4h5v16H4z M9 7h11v13H9 M12 11h5 M12 15h5',
  camara: 'M3 7h13v8H3z M16 10l5-2.5v7L16 12 M7 15v4h5',
  access_point: 'M12 18h.01 M8.5 14.5a5 5 0 0 1 7 0 M5 11a10 10 0 0 1 14 0',
  otro: 'M4 4h16v16H4z M9 12h6',
  desconocido: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M9.5 9a2.5 2.5 0 1 1 3.5 2.3c-.6.3-1 .9-1 1.6V14 M12 17.5v.01',
  switch: 'M3 8h18v8H3z M7 12h.01 M10 12h.01 M13 12h.01 M16 12h.01',
  firewall: 'M3 5h18v14H3z M3 10h18 M3 15h18 M9 5v5 M15 10v5 M9 15v4',
  router: 'M4 14h16v6H4z M8 17h.01 M12 4v10 M8 8l4-4 4 4',
  red: 'M4 6h16v12H4z',
};

function nodeIcon(type: TopologyNode['type']): string {
  if (type === 'access_point') return ICONS.access_point;
  if (type === 'firewall') return ICONS.firewall;
  if (type === 'router') return ICONS.router;
  if (type === 'switch') return ICONS.switch;
  if (type === 'servidor') return ICONS.servidor;
  return ICONS.red;
}

/** Estado de un equipo final para su punto: el de su ficha si se conoce; si no, visto (sano) o documentado (gris). */
function endpointTone(e: TopologyEndpoint): 'sano' | 'advertencia' | 'critico' | 'desconectado' | 'desconocido' {
  if (e.kind === 'documentado') return 'desconectado';
  if (e.machineStatus === 'critico' || e.machineStatus === 'advertencia' || e.machineStatus === 'desconectado')
    return e.machineStatus;
  return e.machineId === null && e.category === 'desconocido' ? 'desconocido' : 'sano';
}

function endpointName(e: TopologyEndpoint): string {
  return (
    e.machineName ??
    e.hostname ??
    (e.category === 'desconocido' ? 'Desconocido' : (e.vendor ?? CATEGORY_LABEL[e.category]))
  );
}

function speed(mbps: number | null): string | null {
  if (!mbps) return null;
  return mbps >= 1000 ? `${mbps / 1000} Gbps` : `${mbps} Mbps`;
}

interface PlacedEndpoint extends TopologyEndpoint {
  key: string;
  x: number;
  y: number;
}

interface Cluster {
  switchId: number;
  busY: number;
  endpoints: PlacedEndpoint[];
  /** "Ver los N": cuántos quedan ocultos y dónde va su botón. */
  hidden: number;
  more: { x: number; y: number } | null;
}

/** Ubica los equipos finales debajo de la última fila, agrupados por el switch donde están. */
function layoutEndpoints(
  nodes: PlacedNode[],
  endpoints: TopologyEndpoint[],
  expanded: ReadonlySet<number>,
  networkHeight: number,
): { clusters: Cluster[]; width: number; height: number } {
  const bySwitch = new Map<number, TopologyEndpoint[]>();
  for (const e of endpoints) bySwitch.set(e.deviceId, [...(bySwitch.get(e.deviceId) ?? []), e]);
  const owners = nodes.filter((n) => bySwitch.has(n.id)).sort((a, b) => a.x - b.x || a.y - b.y);
  const top = MARGIN + networkHeight;
  let x = MARGIN;
  let bottom = top;
  const clusters: Cluster[] = owners.map((owner) => {
    const all = bySwitch.get(owner.id)!;
    const shown = expanded.has(owner.id) || all.length <= EP_SHOWN + 1 ? all : all.slice(0, EP_SHOWN);
    const hidden = all.length - shown.length;
    const slots = shown.length + (hidden > 0 ? 1 : 0);
    const cols = Math.min(EP_COLS, Math.max(1, slots));
    const placed = shown.map((e, i) => ({
      ...e,
      key: `${e.portId}-${e.mac ?? e.machineId}`,
      x: x + EP_GAP_X + (i % cols) * (EP_W + EP_GAP_X),
      y: top + 40 + Math.floor(i / cols) * (EP_H + EP_GAP_Y),
    }));
    const more =
      hidden > 0
        ? {
            x: x + EP_GAP_X + (shown.length % cols) * (EP_W + EP_GAP_X),
            y: top + 40 + Math.floor(shown.length / cols) * (EP_H + EP_GAP_Y),
          }
        : null;
    const rows = Math.ceil(slots / cols);
    bottom = Math.max(bottom, top + 40 + rows * (EP_H + EP_GAP_Y));
    const cluster = { switchId: owner.id, busY: top + 14, endpoints: placed, hidden, more };
    x += EP_GAP_X + cols * (EP_W + EP_GAP_X) + CLUSTER_GAP;
    return cluster;
  });
  return { clusters, width: x + MARGIN, height: bottom + MARGIN };
}

// ------------------------------------------------------------------ vista

type Selection = { kind: 'node'; id: number } | { kind: 'endpoint'; key: string } | null;

/**
 * W-51 Topología (decisión 74): los equipos de red en capas (core arriba) y, debajo de cada switch, los equipos
 * conectados a sus puertos (PC, impresoras, teléfonos, cámaras…), con el detalle de cada uno al tocarlo.
 */
export function TopologyMap({
  onOpenDevice,
  onOpenEquipo,
}: {
  onOpenDevice: (id: number) => void;
  onOpenEquipo?: (id: number) => void;
}) {
  const api = usePanelApi();
  const query = useQuery({ queryKey: ['red', 'topologia'], queryFn: () => api.red.topology() });
  const [branch, setBranch] = useState('');
  const [vlan, setVlan] = useState('');
  const [category, setCategory] = useState('');
  const [withEndpoints, setWithEndpoints] = useState(true);
  const [fit, setFit] = useState(true);
  const [expanded, setExpanded] = useState<ReadonlySet<number>>(new Set());
  const [selection, setSelection] = useState<Selection>(null);
  const svgRef = useRef<SVGSVGElement>(null);

  const data = query.data;
  const branches = useMemo(() => {
    const seen = new Map<string, string>();
    for (const n of data?.nodes ?? []) seen.set(String(n.branchId ?? n.branchName), n.branchName);
    return [...seen.entries()].map(([value, label]) => ({ value, label }));
  }, [data]);
  const vlans = useMemo(
    () =>
      [...new Set((data?.endpoints ?? []).map((e) => e.vlan).filter((v): v is number => v !== null))]
        .sort((a, b) => a - b)
        .map((v) => ({ value: String(v), label: `VLAN ${v}` })),
    [data],
  );

  const view = useMemo(() => {
    if (!data) return null;
    const nodes = data.nodes.filter((n) => !branch || String(n.branchId ?? n.branchName) === branch);
    const ids = new Set(nodes.map((n) => n.id));
    const links = data.links.filter((l) => ids.has(l.sourceId) && ids.has(l.targetId));
    const endpoints = withEndpoints
      ? (data.endpoints ?? []).filter(
          (e) => ids.has(e.deviceId) && (!vlan || String(e.vlan) === vlan) && (!category || e.category === category),
        )
      : [];
    const network = layoutTopology({ nodes, links });
    const eps = layoutEndpoints(network.nodes, endpoints, expanded, network.height);
    const width = Math.max(network.width, eps.width);
    // Centra la parte más angosta.
    const shiftNet = (width - network.width) / 2;
    const shiftEp = (width - eps.width) / 2;
    const placedNodes = network.nodes.map((n) => ({ ...n, x: n.x + shiftNet }));
    const clusters = eps.clusters.map((c) => ({
      ...c,
      endpoints: c.endpoints.map((e) => ({ ...e, x: e.x + shiftEp })),
      more: c.more ? { x: c.more.x + shiftEp, y: c.more.y } : null,
    }));
    return {
      nodes: placedNodes,
      links,
      clusters,
      width,
      height: Math.max(network.height + MARGIN, eps.height),
      total: endpoints.length,
    };
  }, [data, branch, vlan, category, withEndpoints, expanded]);

  if (query.isPending) return <Loading variant="block" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  if (!data || !view || data.nodes.length === 0) return <EmptyState title="Sin equipos de red activos." />;

  const at = new Map(view.nodes.map((n) => [n.id, n]));
  const allEndpoints = view.clusters.flatMap((c) => c.endpoints);
  const selectedNode = selection?.kind === 'node' ? data.nodes.find((n) => n.id === selection.id) : undefined;
  const selectedEndpoint =
    selection?.kind === 'endpoint' ? allEndpoints.find((e) => e.key === selection.key) : undefined;
  const nodeName = (id: number) => data.nodes.find((n) => n.id === id)?.name ?? `#${id}`;

  const exportPng = () => {
    if (svgRef.current) void exportSvgAsPng(svgRef.current, view.width, view.height, 'topologia.png');
  };

  return (
    <div className="topology-view">
      <div className="filters">
        <FilterSelect label="Sucursal" value={branch} options={branches} allLabel="Todas" onChange={setBranch} />
        <FilterSelect label="VLAN" value={vlan} options={vlans} allLabel="Todas" onChange={setVlan} />
        <FilterSelect
          label="Tipo"
          value={category}
          options={(Object.keys(CATEGORY_LABEL) as EndpointCategory[]).map((c) => ({
            value: c,
            label: CATEGORY_LABEL[c],
          }))}
          allLabel="Todos"
          onChange={setCategory}
        />
        <div className="segmented" role="group" aria-label="Qué mostrar">
          <button type="button" aria-pressed={!withEndpoints} onClick={() => setWithEndpoints(false)}>
            Solo red
          </button>
          <button type="button" aria-pressed={withEndpoints} onClick={() => setWithEndpoints(true)}>
            Red + equipos
          </button>
        </div>
        <span className="topology-read">
          {data.macTableReadAt ? (
            <>
              Tabla MAC leída <strong className="mono">{timeAgo(data.macTableReadAt)}</strong>
            </>
          ) : (
            'Sin sondeos SNMP todavía'
          )}
        </span>
        <button type="button" className="btn btn-outline" aria-pressed={fit} onClick={() => setFit(!fit)}>
          {fit ? 'Tamaño real' : 'Ajustar'}
        </button>
        <button type="button" className="btn btn-outline" onClick={exportPng}>
          Exportar PNG
        </button>
      </div>

      <div className="topology-layout">
        <Card
          title="Topología"
          className="topology-card"
          aside={
            <span className="eyebrow mono">
              {view.nodes.length} equipos de red · {view.total} equipos finales
            </span>
          }
        >
          <div className="topology-canvas" data-fit={fit || undefined}>
            <svg
              ref={svgRef}
              viewBox={`0 0 ${view.width} ${view.height}`}
              width={fit ? '100%' : view.width}
              height={fit ? undefined : view.height}
              role="group"
              aria-label="Mapa de topología"
            >
              {/* Enlaces entre equipos de red: codo a media altura. */}
              {view.links.map((l) => {
                const a = at.get(l.sourceId)!;
                const b = at.get(l.targetId)!;
                const [top, bottom] = a.y <= b.y ? [a, b] : [b, a];
                const x1 = top.x + NODE_W / 2;
                const x2 = bottom.x + NODE_W / 2;
                const y1 = top.y + NODE_H;
                const y2 = bottom.y;
                const mid = (y1 + y2) / 2;
                const trunk = l.kind === 'lldp';
                return (
                  <g
                    key={`${l.sourceId}-${l.targetId}`}
                    className="topology-link"
                    data-kind={l.kind}
                    data-trunk={trunk || undefined}
                  >
                    <path d={`M${x1} ${y1} V${mid} H${x2} V${y2}`} />
                    <text x={x2 + 6} y={mid - 6} className="topology-link-label">
                      {top === a ? `${l.sourcePort} ↔ ${l.targetPort}` : `${l.targetPort} ↔ ${l.sourcePort}`}
                    </text>
                    <title>
                      {nodeName(l.sourceId)} {l.sourcePort} ↔ {nodeName(l.targetId)} {l.targetPort} (
                      {trunk ? 'detectado por LLDP' : 'cableado documentado'})
                    </title>
                  </g>
                );
              })}

              {/* De cada switch a sus equipos: bajada, barra y peine hacia cada caja. */}
              {view.clusters.map((c) => {
                const owner = at.get(c.switchId);
                if (!owner) return null;
                const ox = owner.x + NODE_W / 2;
                const xs = [
                  ...c.endpoints.map((e) => e.x - EP_GAP_X / 2),
                  ...(c.more ? [c.more.x - EP_GAP_X / 2] : []),
                ];
                const minX = Math.min(ox, ...xs);
                const maxX = Math.max(ox, ...xs);
                return (
                  <g key={`bus-${c.switchId}`} className="topology-bus">
                    <path d={`M${ox} ${owner.y + NODE_H} V${c.busY} M${minX} ${c.busY} H${maxX}`} />
                    {c.endpoints.map((e) => {
                      const on = selection?.kind === 'endpoint' && selection.key === e.key;
                      return (
                        <path
                          key={e.key}
                          d={`M${e.x - EP_GAP_X / 2} ${c.busY} V${e.y + EP_H / 2} H${e.x}`}
                          data-kind={e.kind}
                          data-selected={on || undefined}
                        />
                      );
                    })}
                    {c.more && (
                      <path d={`M${c.more.x - EP_GAP_X / 2} ${c.busY} V${c.more.y + EP_H / 2} H${c.more.x}`} />
                    )}
                  </g>
                );
              })}

              {view.nodes.map((n) => {
                const on = selection?.kind === 'node' && selection.id === n.id;
                return (
                  <g
                    key={n.id}
                    className="topology-node"
                    data-selected={on || undefined}
                    transform={`translate(${n.x}, ${n.y})`}
                    role="button"
                    tabIndex={0}
                    aria-pressed={on}
                    aria-label={`${n.name}: ${DEVICE_STATUS_LABEL[n.status]}`}
                    onClick={() => setSelection({ kind: 'node', id: n.id })}
                    onDoubleClick={() => onOpenDevice(n.id)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') onOpenDevice(n.id);
                      if (e.key === ' ') {
                        e.preventDefault();
                        setSelection({ kind: 'node', id: n.id });
                      }
                    }}
                  >
                    <rect width={NODE_W} height={NODE_H} rx={4} />
                    <path className="topology-icon" transform="translate(12, 17) scale(1)" d={nodeIcon(n.type)} />
                    <text x={44} y={22} className="topology-name">
                      {n.name}
                    </text>
                    <text x={44} y={38} className="topology-sub">
                      {DEVICE_TYPE_LABEL[n.type]}
                      {n.modelName ? ` · ${n.modelName}` : ''}
                    </text>
                    <text x={44} y={52} className="topology-sub mono">
                      {n.type === 'access_point' && n.clients
                        ? `${n.clients} clientes Wi-Fi`
                        : (n.managementIp ?? n.branchName)}
                    </text>
                    <circle cx={NODE_W - 12} cy={12} r={4.5} data-status={n.status} className="topology-dot" />
                  </g>
                );
              })}

              {allEndpoints.map((e) => {
                const on = selection?.kind === 'endpoint' && selection.key === e.key;
                return (
                  <g
                    key={e.key}
                    className="topology-endpoint"
                    data-kind={e.kind}
                    data-selected={on || undefined}
                    transform={`translate(${e.x}, ${e.y})`}
                    role="button"
                    tabIndex={0}
                    aria-pressed={on}
                    aria-label={`${endpointName(e)}, ${CATEGORY_LABEL[e.category]}, puerto ${e.portLabel}${e.vlan ? `, VLAN ${e.vlan}` : ''}`}
                    onClick={() => setSelection({ kind: 'endpoint', key: e.key })}
                    onKeyDown={(ev) => {
                      if (ev.key === 'Enter' || ev.key === ' ') {
                        ev.preventDefault();
                        setSelection({ kind: 'endpoint', key: e.key });
                      }
                    }}
                  >
                    <rect width={EP_W} height={EP_H} rx={4} />
                    <path className="topology-icon" transform="translate(10, 12)" d={ICONS[e.category]} />
                    <text x={40} y={20} className="topology-name">
                      {endpointName(e)}
                    </text>
                    <text x={40} y={35} className="topology-sub">
                      {CATEGORY_LABEL[e.category]}
                      {e.categoryInferred ? ' ?' : ''}
                    </text>
                    <text x={40} y={49} className="topology-sub mono">
                      {e.ip ?? e.mac ?? '—'}
                    </text>
                    <text x={40} y={62} className="topology-port mono">
                      {e.portLabel}
                      {e.vlan ? ` · VLAN ${e.vlan}` : ''}
                    </text>
                    <circle cx={EP_W - 11} cy={11} r={4.5} data-tone={endpointTone(e)} className="topology-dot" />
                  </g>
                );
              })}

              {view.clusters
                .filter((c) => c.more)
                .map((c) => (
                  <g
                    key={`more-${c.switchId}`}
                    className="topology-more"
                    transform={`translate(${c.more!.x}, ${c.more!.y})`}
                    role="button"
                    tabIndex={0}
                    aria-label={`Ver los ${c.hidden} equipos más de ${nodeName(c.switchId)}`}
                    onClick={() => setExpanded(new Set([...expanded, c.switchId]))}
                    onKeyDown={(ev) => {
                      if (ev.key === 'Enter' || ev.key === ' ') {
                        ev.preventDefault();
                        setExpanded(new Set([...expanded, c.switchId]));
                      }
                    }}
                  >
                    <rect width={EP_W} height={EP_H} rx={4} />
                    <text x={EP_W / 2} y={EP_H / 2 + 4} textAnchor="middle" className="topology-name">
                      + ver {c.hidden} más
                    </text>
                  </g>
                ))}
            </svg>
          </div>
          <div className="topology-legend">
            <span data-legend="trunk">Troncal (LLDP)</span>
            <span data-legend="seen">Visto por SNMP</span>
            <span data-legend="doc">Solo documentado</span>
            <span data-legend="selected">Seleccionado</span>
            <span className="topology-legend-note">Un "?" en el tipo: deducido por el fabricante de la MAC.</span>
          </div>
        </Card>

        <Card title="Detalle" className="topology-detail">
          {selectedNode ? (
            <NodeDetail node={selectedNode} onOpen={() => onOpenDevice(selectedNode.id)} />
          ) : selectedEndpoint ? (
            <EndpointDetail
              key={selectedEndpoint.key}
              endpoint={selectedEndpoint}
              switchName={nodeName(selectedEndpoint.deviceId)}
              onOpenDevice={() => onOpenDevice(selectedEndpoint.deviceId)}
              onOpenEquipo={onOpenEquipo}
            />
          ) : (
            <EmptyState title="Elija un equipo del mapa.">
              Un clic muestra su detalle; doble clic en un equipo de red abre su ficha.
            </EmptyState>
          )}
        </Card>
      </div>
    </div>
  );
}

function NodeDetail({ node, onOpen }: { node: TopologyNode; onOpen: () => void }) {
  const rows: [string, string | null | undefined, boolean?][] = [
    ['Tipo', DEVICE_TYPE_LABEL[node.type]],
    ['Modelo', node.modelName],
    ['IP de gestión', node.managementIp, true],
    ['Sucursal', node.branchName],
    ['Troncales', String(node.trunks), true],
    ['Clientes Wi-Fi', node.type === 'access_point' ? String(node.clients ?? 0) : null, true],
  ];
  return (
    <div className="topology-detail-body">
      <div className="topology-detail-head">
        <strong className="mono">{node.name}</strong>
        <span
          className="status-badge"
          data-status={
            node.status === 'en_linea' ? 'sano' : node.status === 'sin_respuesta' ? 'desconectado' : node.status
          }
        >
          <span className="status-dot" aria-hidden="true" />
          {DEVICE_STATUS_LABEL[node.status]}
        </span>
      </div>
      <dl className="info-list">
        {rows
          .filter(([, v]) => v)
          .map(([label, value, mono]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd className={mono ? 'mono strong' : undefined}>{value}</dd>
            </div>
          ))}
      </dl>
      <div className="topology-detail-actions">
        <button type="button" className="btn btn-outline" onClick={onOpen}>
          Abrir dispositivo
        </button>
      </div>
    </div>
  );
}

function EndpointDetail({
  endpoint: e,
  switchName,
  onOpenDevice,
  onOpenEquipo,
}: {
  endpoint: TopologyEndpoint;
  switchName: string;
  onOpenDevice: () => void;
  onOpenEquipo?: (id: number) => void;
}) {
  const api = usePanelApi();
  const path = useQuery({
    queryKey: ['red', 'ruta', 'puerto', e.portId],
    queryFn: () => api.red.path({ puerto: e.portId }),
  });
  const tone = endpointTone(e);
  const rows: [string, string | null, boolean?][] = [
    ['Puerto', `${e.portLabel}${speed(e.speedMbps) ? ` · ${speed(e.speedMbps)}` : ''}`, true],
    ['VLAN', e.vlan === null ? null : String(e.vlan), true],
    ['IP', e.ip, true],
    ['MAC', e.mac, true],
    ['Fabricante', e.vendor],
    ['Agente', e.hasAgent ? 'Sí · Centinela' : e.machineId ? 'No · registrado a mano' : 'No · solo SNMP'],
    ['Primera vez', e.firstSeenAt ? dateTime(e.firstSeenAt) : null, true],
    ['Última vez', e.lastSeenAt ? timeAgo(e.lastSeenAt) : null, true],
  ];
  return (
    <div className="topology-detail-body">
      <div className="topology-detail-head">
        <strong className="mono">{endpointName(e)}</strong>
        <span className="status-badge" data-status={tone === 'desconocido' ? 'advertencia' : tone}>
          <span className="status-dot" aria-hidden="true" />
          {e.kind === 'documentado'
            ? 'Sin verse'
            : tone === 'desconocido'
              ? 'Sin identificar'
              : tone === 'sano'
                ? 'En línea'
                : tone === 'critico'
                  ? 'Crítico'
                  : tone === 'advertencia'
                    ? 'Advertencia'
                    : 'Desconectado'}
        </span>
      </div>
      <p className="muted topology-detail-type">
        {CATEGORY_LABEL[e.category]}
        {e.categoryInferred ? ' · deducido por el fabricante de la MAC' : ''}
      </p>
      {path.data && path.data.length > 0 && (
        <ol className="topology-path mono" aria-label="Ruta completa">
          {[...path.data].map((hop, i) => (
            <li key={i}>
              {hop.name}
              {hop.detail ? ` ${hop.detail}` : ''}
            </li>
          ))}
        </ol>
      )}
      {path.data && path.data.length === 0 && (
        <p className="muted topology-detail-type">
          Conectado a{' '}
          <span className="mono">
            {switchName} {e.portLabel}
          </span>
          . El cableado de este puerto no está documentado.
        </p>
      )}
      <dl className="info-list">
        {rows
          .filter(([, v]) => v)
          .map(([label, value, mono]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd className={mono ? 'mono strong' : undefined}>{value}</dd>
            </div>
          ))}
      </dl>
      <div className="topology-detail-actions">
        <button type="button" className="btn btn-outline" onClick={onOpenDevice}>
          Ver puerto
        </button>
        {e.machineId !== null && onOpenEquipo && (
          <button type="button" className="btn btn-outline" onClick={() => onOpenEquipo(e.machineId!)}>
            Abrir ficha
          </button>
        )}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ exportar

const EXPORT_PROPS = [
  'fill',
  'stroke',
  'stroke-width',
  'stroke-dasharray',
  'font-family',
  'font-size',
  'font-weight',
  'opacity',
] as const;

/** PNG del mapa tal como se ve: copia los estilos calculados (variables de tema incluidas) a una copia del SVG. */
async function exportSvgAsPng(svg: SVGSVGElement, width: number, height: number, name: string): Promise<void> {
  const clone = svg.cloneNode(true) as SVGSVGElement;
  const originals = [svg, ...svg.querySelectorAll('*')];
  const copies = [clone, ...clone.querySelectorAll('*')];
  originals.forEach((el, i) => {
    const style = getComputedStyle(el);
    const target = copies[i] as SVGElement;
    for (const prop of EXPORT_PROPS) target.style.setProperty(prop, style.getPropertyValue(prop));
  });
  clone.setAttribute('width', String(width));
  clone.setAttribute('height', String(height));
  clone.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  const markup = new XMLSerializer().serializeToString(clone);
  const image = new Image();
  const url = URL.createObjectURL(new Blob([markup], { type: 'image/svg+xml' }));
  try {
    await new Promise<void>((resolve, reject) => {
      image.onload = () => resolve();
      image.onerror = () => reject(new Error('No se pudo dibujar el mapa.'));
      image.src = url;
    });
    const canvas = document.createElement('canvas');
    const scale = 2;
    canvas.width = width * scale;
    canvas.height = height * scale;
    const ctx = canvas.getContext('2d')!;
    ctx.fillStyle = getComputedStyle(document.body).backgroundColor || '#0d1115';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.scale(scale, scale);
    ctx.drawImage(image, 0, 0);
    const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/png'));
    if (!blob) return;
    const bytes = new Uint8Array(await blob.arrayBuffer());
    if (window.monitoreo) await window.monitoreo.saveFile(name, bytes);
    else {
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.download = name;
      link.click();
    }
  } finally {
    URL.revokeObjectURL(url);
  }
}

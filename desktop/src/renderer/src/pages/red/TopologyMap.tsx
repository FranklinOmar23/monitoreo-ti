import { useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { EndpointCategory, Topology, TopologyEndpoint, TopologyNode } from '@monitoreoti/shared';
import { Card } from '@components/Card';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { dateTime, timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { DEVICE_STATUS_LABEL, DEVICE_TYPE_LABEL } from './netLabels';

// ------------------------------------------------------------------ medidas del mapa

const NODE_W = 200;
const NODE_H = 58;
const GAP_X = 36;
const GAP_Y = 84;
const EP_W = 158;
const EP_H = 56;
const EP_GAP_X = 18;
const EP_GAP_Y = 10;
/** Columnas de equipos finales bajo un switch, y cuántos se muestran antes de "ver los N". */
const EP_COLS = 2;
const EP_SHOWN = 7;
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

// ------------------------------------------------------------------ árbol (rediseño)

/** Identificador de un equipo final en el mapa (su puerto y su MAC o ficha). */
export const endpointKey = (e: Pick<TopologyEndpoint, 'portId' | 'mac' | 'machineId'>) =>
  `${e.portId}-${e.mac ?? e.machineId}`;

/** Resultado del buscador de la topología. */
export type SearchHit =
  | { kind: 'node'; id: number; label: string; sub: string }
  | { kind: 'endpoint'; key: string; endpoint: TopologyEndpoint; label: string; sub: string };

/**
 * Busca por IP, nombre (de la ficha, el hostname o el equipo de red) o MAC (con o sin separadores) en los equipos de
 * red y los equipos finales. Primero lo que coincide exacto, después lo que empieza igual y al final lo que contiene.
 */
export function searchTopology(
  topology: Pick<Topology, 'nodes'> & { endpoints?: TopologyEndpoint[] | null },
  text: string,
  limit = 8,
): SearchHit[] {
  const q = text.trim().toLowerCase();
  if (q.length < 2) return [];
  // Parece MAC si lleva letras hex o separadores de MAC, o son 6+ dígitos seguidos (una IP lleva puntos).
  const hex =
    /^[0-9a-f:.-]+$/.test(q) && (/[a-f:-]/.test(q) || /^[0-9a-f]{6,}$/.test(q)) ? q.replace(/[^0-9a-f]/g, '') : '';
  const rank = (fields: (string | null | undefined)[], macs: (string | null | undefined)[]) => {
    let best = Infinity;
    for (const raw of fields) {
      const f = raw?.toLowerCase();
      if (!f) continue;
      if (f === q) best = Math.min(best, 0);
      else if (f.startsWith(q)) best = Math.min(best, 1);
      else if (f.includes(q)) best = Math.min(best, 2);
    }
    if (hex.length >= 4)
      for (const raw of macs) {
        const m = raw?.toLowerCase().replace(/[^0-9a-f]/g, '');
        if (!m) continue;
        if (m === hex) best = Math.min(best, 0);
        else if (m.includes(hex)) best = Math.min(best, 2);
      }
    return best;
  };
  const names = new Map(topology.nodes.map((n) => [n.id, n.name]));
  const hits: { hit: SearchHit; score: number }[] = [];
  for (const n of topology.nodes) {
    const score = rank([n.name, n.managementIp], []);
    if (score < Infinity)
      hits.push({
        score,
        hit: {
          kind: 'node',
          id: n.id,
          label: n.name,
          sub: `${DEVICE_TYPE_LABEL[n.type]} · ${n.managementIp ?? n.branchName}`,
        },
      });
  }
  for (const e of topology.endpoints ?? []) {
    const score = rank([e.machineName, e.hostname, e.ip], [e.mac]);
    if (score < Infinity)
      hits.push({
        score,
        hit: {
          kind: 'endpoint',
          key: endpointKey(e),
          endpoint: e,
          label: endpointName(e),
          sub: `${e.ip ?? e.mac ?? '—'} · ${names.get(e.deviceId) ?? 'switch'} ${e.portLabel}`,
        },
      });
  }
  return hits
    .sort((a, b) => a.score - b.score || a.hit.label.localeCompare(b.hit.label))
    .slice(0, limit)
    .map((h) => h.hit);
}

interface PlacedEndpoint extends TopologyEndpoint {
  key: string;
  x: number;
  y: number;
}

interface Cluster {
  switchId: number;
  /** Altura de la barra que reparte el cable a los equipos (en el hueco sobre el grupo). */
  busY: number;
  endpoints: PlacedEndpoint[];
  /** "Ver los N": cuántos quedan ocultos y dónde va su botón. */
  hidden: number;
  more: { x: number; y: number } | null;
}

export interface TreeLayout {
  nodes: PlacedNode[];
  clusters: Cluster[];
  /** Padre de cada equipo de red en el árbol (hacia el core); los sueltos no tienen. */
  parent: Map<number, number>;
  width: number;
  height: number;
}

/** Clave de un enlace sin importar el sentido. */
export const linkKey = (a: number, b: number) => (a < b ? `${a}-${b}` : `${b}-${a}`);

/**
 * El mapa como árbol: el core arriba y, debajo de cada equipo, sus equipos hijos y el grupo de equipos finales de sus
 * puertos, cada rama en su propia columna (nada cuelga lejos de su switch). Los equipos sin enlaces van al final.
 */
export function layoutTree(
  topology: Pick<Topology, 'nodes' | 'links'>,
  endpoints: TopologyEndpoint[],
  expanded: ReadonlySet<number>,
): TreeLayout {
  const { nodes: leveled } = layoutTopology(topology);
  const level = new Map(leveled.map((n) => [n.id, n.level]));
  const byId = new Map(leveled.map((n) => [n.id, n]));
  const linked = new Set(topology.links.flatMap((l) => [l.sourceId, l.targetId]));

  // Padre: un vecino de la capa de arriba (mejor por LLDP que por cableado documentado).
  const parent = new Map<number, number>();
  const sortedLinks = [...topology.links].sort((a, b) => (a.kind === 'lldp' ? 0 : 1) - (b.kind === 'lldp' ? 0 : 1));
  for (const n of leveled) {
    if (n.level === 0 || !linked.has(n.id)) continue;
    for (const l of sortedLinks) {
      const other = l.sourceId === n.id ? l.targetId : l.targetId === n.id ? l.sourceId : null;
      if (other !== null && level.get(other) === n.level - 1) {
        parent.set(n.id, other);
        break;
      }
    }
  }
  const children = new Map<number, number[]>();
  for (const [child, p] of parent) children.set(p, [...(children.get(p) ?? []), child]);
  for (const list of children.values()) list.sort((a, b) => byId.get(a)!.name.localeCompare(byId.get(b)!.name));

  // Grupos de equipos finales por switch.
  const bySwitch = new Map<number, TopologyEndpoint[]>();
  for (const e of endpoints) bySwitch.set(e.deviceId, [...(bySwitch.get(e.deviceId) ?? []), e]);
  const clusterSize = (id: number) => {
    const all = bySwitch.get(id);
    if (!all?.length) return null;
    const shown = expanded.has(id) || all.length <= EP_SHOWN + 1 ? all.length : EP_SHOWN;
    const slots = shown + (shown < all.length ? 1 : 0);
    const cols = Math.min(EP_COLS, slots);
    const rows = Math.ceil(slots / cols);
    return {
      shown,
      cols,
      w: cols * EP_W + (cols - 1) * EP_GAP_X + EP_GAP_X,
      h: rows * EP_H + (rows - 1) * EP_GAP_Y,
    };
  };

  // Ancho de cada rama: el del equipo o la suma de lo que cuelga de él.
  const widths = new Map<number, number>();
  const widthOf = (id: number): number => {
    if (widths.has(id)) return widths.get(id)!;
    const items = [...(children.get(id) ?? []).map(widthOf), clusterSize(id)?.w ?? 0].filter((w) => w > 0);
    const inner = items.reduce((sum, w) => sum + w, 0) + Math.max(0, items.length - 1) * GAP_X;
    const w = Math.max(NODE_W, inner);
    widths.set(id, w);
    return w;
  };

  // Filas: profundidad del equipo en el árbol; su grupo va en la fila siguiente.
  const depth = new Map<number, number>();
  const setDepth = (id: number, d: number) => {
    depth.set(id, d);
    for (const c of children.get(id) ?? []) setDepth(c, d + 1);
  };
  const roots = leveled.filter((n) => !parent.has(n.id) && linked.has(n.id)).map((n) => n.id);
  const loose = leveled.filter((n) => !linked.has(n.id)).map((n) => n.id);
  roots.forEach((r) => setDepth(r, 0));
  const mainDepth = Math.max(-1, ...[...depth.entries()].map(([id, d]) => d + (clusterSize(id) ? 1 : 0)));
  loose.forEach((r) => setDepth(r, mainDepth + 1));

  const rowHeight = new Map<number, number>();
  const grow = (d: number, h: number) => rowHeight.set(d, Math.max(rowHeight.get(d) ?? 0, h));
  for (const [id, d] of depth) {
    grow(d, NODE_H);
    const c = clusterSize(id);
    if (c) grow(d + 1, c.h);
  }
  const rowTop = new Map<number, number>();
  let y = MARGIN;
  for (const d of [...rowHeight.keys()].sort((a, b) => a - b)) {
    rowTop.set(d, y);
    y += rowHeight.get(d)! + GAP_Y;
  }

  const placed: PlacedNode[] = [];
  const clusters: Cluster[] = [];
  const place = (id: number, left: number) => {
    const w = widthOf(id);
    const d = depth.get(id)!;
    const kids = children.get(id) ?? [];
    const c = clusterSize(id);
    const items = [
      ...kids.map((k) => ({ kind: 'node' as const, id: k, w: widthOf(k) })),
      ...(c ? [{ kind: 'cluster' as const, id, w: c.w }] : []),
    ];
    const inner = items.reduce((sum, i) => sum + i.w, 0) + Math.max(0, items.length - 1) * GAP_X;
    let x = left + (w - inner) / 2;
    const centers: number[] = [];
    for (const item of items) {
      if (item.kind === 'node') {
        place(item.id, x);
        const child = placed.find((p) => p.id === item.id)!;
        centers.push(child.x + NODE_W / 2);
      } else {
        const all = bySwitch.get(id)!;
        const shown = all.slice(0, c!.shown);
        const top = rowTop.get(d + 1)!;
        const ex = x + EP_GAP_X;
        clusters.push({
          switchId: id,
          busY: top - Math.min(26, GAP_Y / 2),
          endpoints: shown.map((e, i) => ({
            ...e,
            key: endpointKey(e),
            x: ex + (i % c!.cols) * (EP_W + EP_GAP_X),
            y: top + Math.floor(i / c!.cols) * (EP_H + EP_GAP_Y),
          })),
          hidden: all.length - shown.length,
          more:
            shown.length < all.length
              ? {
                  x: ex + (shown.length % c!.cols) * (EP_W + EP_GAP_X),
                  y: top + Math.floor(shown.length / c!.cols) * (EP_H + EP_GAP_Y),
                }
              : null,
        });
        centers.push(x + c!.w / 2);
      }
      x += item.w + GAP_X;
    }
    // El equipo queda centrado sobre lo que cuelga de él.
    const center = centers.length > 0 ? (Math.min(...centers) + Math.max(...centers)) / 2 : left + w / 2;
    placed.push({ ...byId.get(id)!, x: center - NODE_W / 2, y: rowTop.get(d)! });
  };

  let left = MARGIN;
  for (const r of roots) {
    place(r, left);
    left += widthOf(r) + GAP_X * 2;
  }
  let width = left;
  left = MARGIN;
  for (const r of loose) {
    place(r, left);
    left += widthOf(r) + GAP_X;
  }
  width = Math.max(width, left) + MARGIN;
  return { nodes: placed, clusters, parent, width, height: y - GAP_Y + MARGIN };
}

/** La ruta de un equipo de red hasta el core (él primero). */
export function pathToRoot(id: number, parent: ReadonlyMap<number, number>): number[] {
  const path = [id];
  const seen = new Set(path);
  let current = parent.get(id);
  while (current !== undefined && !seen.has(current)) {
    path.push(current);
    seen.add(current);
    current = parent.get(current);
  }
  return path;
}

// ------------------------------------------------------------------ vista

type Selection = { kind: 'node'; id: number } | { kind: 'endpoint'; key: string } | null;

interface ViewBox {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** Escala mínima al abrir: más chico no se lee (se recorre arrastrando o con el zoom). */
const READABLE_SCALE = 0.55;
const MIN_SCALE = 0.12;
const MAX_SCALE = 2.5;
const CANVAS_HEIGHT = 560;

/** Paso de la ruta para el detalle: equipo y, si se conoce, por qué puerto sale hacia el siguiente. */
export interface RouteStep {
  name: string;
  port: string | null;
}

/**
 * W-51 Topología (decisiones 74 y 81): un árbol con el core arriba y, debajo de cada equipo de red, sus equipos y los
 * equipos finales de sus puertos. Se acerca con la rueda, se recorre arrastrando, y al elegir algo se resalta su ruta
 * hasta el core (lo demás se atenúa).
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
  const [expanded, setExpanded] = useState<ReadonlySet<number>>(new Set());
  const [selection, setSelection] = useState<Selection>(null);
  const [search, setSearch] = useState('');
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchActive, setSearchActive] = useState(0);
  /** Lo elegido en el buscador: se encuadra en cuanto el mapa lo dibuja. */
  const [focusTarget, setFocusTarget] = useState<
    { kind: 'node'; id: number } | { kind: 'endpoint'; key: string; deviceId: number } | null
  >(null);
  const [box, setBox] = useState<ViewBox | null>(null);
  const svgRef = useRef<SVGSVGElement>(null);
  const canvasRef = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; y: number; box: ViewBox; moved: boolean } | null>(null);
  const [dragging, setDragging] = useState(false);
  const [showingAll, setShowingAll] = useState(false);
  /** Tamaño del lienzo en pantalla (para encuadrar y para el zoom). */
  const [size, setSize] = useState({ w: 1000, h: CANVAS_HEIGHT });

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
    const tree = layoutTree({ nodes, links }, endpoints, expanded);
    return { ...tree, links, total: endpoints.length };
  }, [data, branch, vlan, category, withEndpoints, expanded]);

  /** Encuadre: todo el mapa a lo ancho, pero sin bajar de un tamaño legible (arriba y al centro). */
  const fitBox = (all: boolean): ViewBox | null => {
    if (!view) return null;
    const { w: width, h: height } = size;
    const scale = Math.min(width / view.width, height / view.height);
    const s = all ? Math.max(MIN_SCALE, scale) : Math.max(Math.min(scale, 1), READABLE_SCALE);
    const w = width / s;
    const h = height / s;
    return { x: (view.width - w) / 2, y: s === scale || all ? (view.height - h) / 2 : 0, w, h };
  };
  // Al cambiar lo que se muestra (filtros, ver más), se vuelve a encuadrar.
  const layoutKey = view ? `${view.width}x${view.height}@${size.w}x${size.h}` : '';
  const [framedKey, setFramedKey] = useState('');
  if (view && framedKey !== layoutKey) {
    setFramedKey(layoutKey);
    setBox(fitBox(false));
    setShowingAll(false);
  }

  // Encuadre de lo buscado (cuando el mapa ya lo dibujó): si el equipo final está entre los "ver N más", primero se
  // abre ese grupo y se encuadra en la vuelta siguiente.
  if (focusTarget && view) {
    let center: { x: number; y: number } | null = null;
    let pending = false;
    if (focusTarget.kind === 'node') {
      const n = view.nodes.find((p) => p.id === focusTarget.id);
      if (n) center = { x: n.x + NODE_W / 2, y: n.y + NODE_H / 2 };
    } else {
      const e = view.clusters.flatMap((c) => c.endpoints).find((p) => p.key === focusTarget.key);
      if (e) center = { x: e.x + EP_W / 2, y: e.y + EP_H / 2 };
      else if (!expanded.has(focusTarget.deviceId) && view.clusters.some((c) => c.switchId === focusTarget.deviceId)) {
        setExpanded(new Set([...expanded, focusTarget.deviceId]));
        pending = true;
      }
    }
    if (!pending) {
      setFocusTarget(null);
      if (center) {
        // Un poco por encima del centro: se ve también el camino hacia el switch.
        setBox({ x: center.x - size.w / 2, y: center.y - size.h * 0.62, w: size.w, h: size.h });
        setShowingAll(false);
      }
    }
  }

  const zoom = (factor: number, cx?: number, cy?: number) =>
    setBox((b) => {
      if (!b) return b;
      const scale = size.w / b.w;
      const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale * factor));
      const w = b.w * (scale / next);
      const h = b.h * (scale / next);
      const px = cx ?? b.x + b.w / 2;
      const py = cy ?? b.y + b.h / 2;
      return { x: px - ((px - b.x) * w) / b.w, y: py - ((py - b.y) * h) / b.h, w, h };
    });

  // Mide el lienzo al abrir y cuando cambia de tamaño (ventana o panel de detalle).
  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;
    const measure = () => {
      if (el.clientWidth > 0) setSize({ w: el.clientWidth, h: el.clientHeight || CANVAS_HEIGHT });
    };
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, [query.isSuccess]);

  // La rueda acerca o aleja donde está el puntero (sin desplazar la página).
  useEffect(() => {
    const el = canvasRef.current;
    if (!el) return;
    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      const rect = el.getBoundingClientRect();
      setBox((b) => {
        if (!b || rect.width === 0) return b;
        const px = b.x + ((event.clientX - rect.left) / rect.width) * b.w;
        const py = b.y + ((event.clientY - rect.top) / rect.height) * b.h;
        const factor = event.deltaY < 0 ? 1.15 : 1 / 1.15;
        const scale = rect.width / b.w;
        const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale * factor));
        const w = b.w * (scale / next);
        const h = b.h * (scale / next);
        return { x: px - ((px - b.x) * w) / b.w, y: py - ((py - b.y) * h) / b.h, w, h };
      });
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [query.isSuccess]);

  const hits = useMemo(() => (data ? searchTopology(data, search) : []), [data, search]);

  /** Muestra lo buscado: quita los filtros que lo ocultan, lo elige (resalta su ruta) y lo centra. */
  const choose = (hit: SearchHit | undefined) => {
    if (!hit || !data) return;
    setSearch(hit.label);
    setSearchOpen(false);
    const deviceId = hit.kind === 'node' ? hit.id : hit.endpoint.deviceId;
    const owner = data.nodes.find((n) => n.id === deviceId);
    if (branch && owner && String(owner.branchId ?? owner.branchName) !== branch) setBranch('');
    if (hit.kind === 'endpoint') {
      const e = hit.endpoint;
      if (vlan && String(e.vlan) !== vlan) setVlan('');
      if (category && e.category !== category) setCategory('');
      setWithEndpoints(true);
      setSelection({ kind: 'endpoint', key: hit.key });
      setFocusTarget({ kind: 'endpoint', key: hit.key, deviceId });
    } else {
      setSelection({ kind: 'node', id: hit.id });
      setFocusTarget({ kind: 'node', id: hit.id });
    }
  };

  if (query.isPending) return <Loading variant="block" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  if (!data || !view || data.nodes.length === 0) return <EmptyState title="Sin equipos de red activos." />;

  const at = new Map(view.nodes.map((n) => [n.id, n]));
  const allEndpoints = view.clusters.flatMap((c) => c.endpoints);
  const selectedNode = selection?.kind === 'node' ? data.nodes.find((n) => n.id === selection.id) : undefined;
  const selectedEndpoint =
    selection?.kind === 'endpoint' ? allEndpoints.find((e) => e.key === selection.key) : undefined;
  const nodeName = (id: number) => data.nodes.find((n) => n.id === id)?.name ?? `#${id}`;

  // Ruta resaltada: del equipo elegido (o del switch del equipo final) hasta el core.
  const startId = selectedNode?.id ?? selectedEndpoint?.deviceId;
  const pathIds = startId !== undefined ? pathToRoot(startId, view.parent) : [];
  const onPath = new Set(pathIds);
  const pathLinks = new Set(pathIds.slice(1).map((id, i) => linkKey(pathIds[i]!, id)));
  const focused = pathIds.length > 0;
  const route: RouteStep[] = [
    ...(selectedEndpoint ? [{ name: endpointName(selectedEndpoint), port: null }] : []),
    ...pathIds.map((id, i) => {
      const next = pathIds[i + 1];
      const link =
        next === undefined ? undefined : view.links.find((l) => linkKey(l.sourceId, l.targetId) === linkKey(id, next));
      const port = link ? (link.sourceId === id ? link.sourcePort : link.targetPort) : null;
      return { name: nodeName(id), port: i === 0 && selectedEndpoint ? selectedEndpoint.portLabel : port };
    }),
  ];

  const exportPng = () => {
    if (svgRef.current) void exportSvgAsPng(svgRef.current, view.width, view.height, 'topologia.png');
  };

  const linkPath = (a: PlacedNode, b: PlacedNode) => {
    const [top, bottom] = a.y <= b.y ? [a, b] : [b, a];
    const x1 = top.x + NODE_W / 2;
    const x2 = bottom.x + NODE_W / 2;
    const y1 = top.y + NODE_H;
    const y2 = bottom.y;
    const mid = y2 - GAP_Y * 0.62;
    return { d: `M${x1} ${y1} V${mid} H${x2} V${y2}`, labelX: x2 + 6, labelY: mid - 6, top };
  };

  return (
    <div className="topology-view">
      <div className="filters">
        <div className="topology-search">
          <label className="search filter-search">
            <Icon name="search" />
            <input
              type="search"
              value={search}
              placeholder="Buscar IP, nombre o MAC"
              aria-label="Buscar en la topología"
              role="combobox"
              aria-expanded={searchOpen && search.trim().length >= 2}
              aria-controls="topology-search-results"
              aria-autocomplete="list"
              onChange={(event) => {
                setSearch(event.target.value);
                setSearchOpen(true);
                setSearchActive(0);
              }}
              onFocus={() => setSearchOpen(true)}
              onBlur={() => setSearchOpen(false)}
              onKeyDown={(event) => {
                if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
                  event.preventDefault();
                  setSearchOpen(true);
                  const step = event.key === 'ArrowDown' ? 1 : -1;
                  setSearchActive((i) => (hits.length ? (i + step + hits.length) % hits.length : 0));
                } else if (event.key === 'Enter') {
                  event.preventDefault();
                  choose(hits[searchActive] ?? hits[0]);
                } else if (event.key === 'Escape') {
                  setSearchOpen(false);
                }
              }}
            />
          </label>
          {searchOpen && search.trim().length >= 2 && (
            <ul id="topology-search-results" className="topology-search-results" role="listbox">
              {hits.length === 0 ? (
                <li className="topology-search-empty">Nada con «{search.trim()}» en la topología.</li>
              ) : (
                hits.map((hit, i) => (
                  <li key={hit.kind === 'node' ? `n${hit.id}` : `e${hit.key}`}>
                    <button
                      type="button"
                      role="option"
                      aria-selected={i === searchActive}
                      // Antes de que el campo pierda el foco (y se cierre la lista).
                      onMouseDown={(event) => event.preventDefault()}
                      onClick={() => choose(hit)}
                    >
                      <span className="topology-search-kind">{hit.kind === 'node' ? 'Red' : 'Equipo'}</span>
                      <strong className="mono">{hit.label}</strong>
                      <span className="mono muted">{hit.sub}</span>
                    </button>
                  </li>
                ))
              )}
            </ul>
          )}
        </div>
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
          <div
            ref={canvasRef}
            className="topology-canvas"
            data-dragging={dragging || undefined}
            style={{ height: CANVAS_HEIGHT }}
            onPointerDown={(event) => {
              if (!box || event.button !== 0) return;
              drag.current = { x: event.clientX, y: event.clientY, box, moved: false };
            }}
            onPointerMove={(event) => {
              const d = drag.current;
              const el = canvasRef.current;
              if (!d || !el) return;
              const dx = event.clientX - d.x;
              const dy = event.clientY - d.y;
              if (!d.moved && Math.hypot(dx, dy) < 4) return;
              if (!d.moved) {
                el.setPointerCapture?.(event.pointerId);
                setDragging(true);
              }
              d.moved = true;
              const scale = el.clientWidth / d.box.w;
              setBox({ ...d.box, x: d.box.x - dx / scale, y: d.box.y - dy / scale });
            }}
            onPointerUp={() => {
              // Un arrastre no cuenta como clic sobre lo que quedó debajo.
              const moved = drag.current?.moved;
              setDragging(false);
              setTimeout(() => {
                if (drag.current?.moved === moved) drag.current = null;
              }, 0);
            }}
            onClickCapture={(event) => {
              if (drag.current?.moved) {
                event.stopPropagation();
                drag.current = null;
              }
            }}
          >
            <div className="topology-zoom" role="group" aria-label="Zoom">
              <button
                type="button"
                className="btn btn-outline btn-sm"
                aria-label="Acercar"
                onClick={() => {
                  zoom(1.25);
                  setShowingAll(false);
                }}
              >
                +
              </button>
              <button
                type="button"
                className="btn btn-outline btn-sm"
                aria-label="Alejar"
                onClick={() => zoom(1 / 1.25)}
              >
                −
              </button>
              <button
                type="button"
                className="btn btn-outline btn-sm"
                aria-pressed={showingAll}
                onClick={() => {
                  setBox(fitBox(!showingAll));
                  setShowingAll(!showingAll);
                }}
              >
                {showingAll ? 'Tamaño legible' : 'Ver todo'}
              </button>
            </div>
            <svg
              ref={svgRef}
              viewBox={box ? `${box.x} ${box.y} ${box.w} ${box.h}` : `0 0 ${view.width} ${view.height}`}
              width="100%"
              height="100%"
              preserveAspectRatio="xMidYMin meet"
              role="group"
              aria-label="Mapa de topología"
              data-focus={focused || undefined}
              onClick={(event) => {
                if (event.target === event.currentTarget) setSelection(null);
              }}
            >
              {/* Enlaces entre equipos de red. */}
              {view.links.map((l) => {
                const a = at.get(l.sourceId);
                const b = at.get(l.targetId);
                if (!a || !b) return null;
                const { d, labelX, labelY, top } = linkPath(a, b);
                const trunk = l.kind === 'lldp';
                const lit = pathLinks.has(linkKey(l.sourceId, l.targetId));
                return (
                  <g
                    key={`${l.sourceId}-${l.targetId}`}
                    className="topology-link"
                    data-kind={l.kind}
                    data-trunk={trunk || undefined}
                    data-path={lit || undefined}
                  >
                    <path d={d} />
                    {lit && <path d={d} className="topology-flow" />}
                    <text x={labelX} y={labelY} className="topology-link-label">
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
                const chosen = c.endpoints.find((e) => e.key === selectedEndpoint?.key);
                const comb = (x: number, y: number) => `M${x - EP_GAP_X / 2} ${c.busY} V${y + EP_H / 2} H${x}`;
                return (
                  <g key={`bus-${c.switchId}`} className="topology-bus" data-path={chosen ? true : undefined}>
                    <path d={`M${ox} ${owner.y + NODE_H} V${c.busY} M${minX} ${c.busY} H${maxX}`} />
                    {c.endpoints.map((e) => (
                      <path key={e.key} d={comb(e.x, e.y)} data-kind={e.kind} />
                    ))}
                    {c.more && <path d={comb(c.more.x, c.more.y)} />}
                    {chosen && (
                      <path
                        className="topology-flow"
                        d={`M${ox} ${owner.y + NODE_H} V${c.busY} H${chosen.x - EP_GAP_X / 2} V${chosen.y + EP_H / 2} H${chosen.x}`}
                      />
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
                    data-path={onPath.has(n.id) || undefined}
                    data-status={n.status}
                    transform={`translate(${n.x}, ${n.y})`}
                    role="button"
                    tabIndex={0}
                    aria-pressed={on}
                    aria-label={`${n.name}: ${DEVICE_STATUS_LABEL[n.status]}`}
                    onClick={() => setSelection(on ? null : { kind: 'node', id: n.id })}
                    onDoubleClick={() => onOpenDevice(n.id)}
                    onKeyDown={(e) => {
                      if (e.key === 'Enter') onOpenDevice(n.id);
                      if (e.key === ' ') {
                        e.preventDefault();
                        setSelection({ kind: 'node', id: n.id });
                      }
                      if (e.key === 'Escape') setSelection(null);
                    }}
                  >
                    <rect width={NODE_W} height={NODE_H} rx={6} />
                    <rect className="topology-stripe" width={4} height={NODE_H} rx={2} />
                    <path className="topology-icon" transform="translate(14, 17)" d={nodeIcon(n.type)} />
                    <text x={46} y={22} className="topology-name">
                      {n.name}
                    </text>
                    <text x={46} y={38} className="topology-sub">
                      {DEVICE_TYPE_LABEL[n.type]}
                      {n.modelName ? ` · ${n.modelName}` : ''}
                    </text>
                    <text x={46} y={52} className="topology-sub mono">
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
                    data-path={on || undefined}
                    transform={`translate(${e.x}, ${e.y})`}
                    role="button"
                    tabIndex={0}
                    aria-pressed={on}
                    aria-label={`${endpointName(e)}, ${CATEGORY_LABEL[e.category]}, puerto ${e.portLabel}${e.vlan ? `, VLAN ${e.vlan}` : ''}`}
                    onClick={() => setSelection(on ? null : { kind: 'endpoint', key: e.key })}
                    onKeyDown={(ev) => {
                      if (ev.key === 'Enter' || ev.key === ' ') {
                        ev.preventDefault();
                        setSelection({ kind: 'endpoint', key: e.key });
                      }
                      if (ev.key === 'Escape') setSelection(null);
                    }}
                  >
                    <rect width={EP_W} height={EP_H} rx={6} />
                    <path className="topology-icon" transform="translate(10, 10)" d={ICONS[e.category]} />
                    <text x={40} y={19} className="topology-name">
                      {endpointName(e)}
                    </text>
                    <text x={40} y={34} className="topology-sub mono">
                      {e.ip ?? e.mac ?? '—'}
                    </text>
                    <text x={40} y={49} className="topology-port mono">
                      {e.portLabel}
                      {e.vlan ? ` · VLAN ${e.vlan}` : ''}
                      {e.categoryInferred ? ' · ?' : ''}
                    </text>
                    <circle cx={EP_W - 11} cy={11} r={4} data-tone={endpointTone(e)} className="topology-dot" />
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
                    <rect width={EP_W} height={EP_H} rx={6} />
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
            <span data-legend="selected">Ruta del elegido</span>
            <span className="topology-legend-note">
              Rueda: zoom · arrastrar: moverse · un "?": tipo deducido por el fabricante de la MAC.
            </span>
          </div>
        </Card>

        <Card title="Detalle" className="topology-detail">
          {route.length > 1 && (
            <ol className="topology-route" aria-label="Ruta hasta el core">
              {route.map((step, i) => (
                <li key={`${step.name}-${i}`}>
                  <strong className="mono">{step.name}</strong>
                  {step.port && <span className="mono muted"> · {step.port}</span>}
                </li>
              ))}
            </ol>
          )}
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
              Búsquelo por IP, nombre o MAC, o haga clic en él: se muestra su detalle y se resalta su ruta hasta el
              core; doble clic en un equipo de red abre su ficha.
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
  // Todo el mapa, aunque en pantalla se vea una parte (zoom).
  clone.setAttribute('viewBox', `0 0 ${width} ${height}`);
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

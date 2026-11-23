import { useEffect, useMemo, useRef, useState } from 'react';
import { useQueries } from '@tanstack/react-query';
import { Canvas, useThree, type ThreeEvent } from '@react-three/fiber';
import { Edges, Html, OrbitControls } from '@react-three/drei';
import type { RackDetail, RackItem, RackPlacementInput } from '@monitoreoti/shared';
import { usePanelApi } from '../../data/PanelData';
import { DEVICE_STATUS_LABEL } from './netLabels';
import {
  cellAt,
  cellCenter,
  deviceBoxes,
  layoutRoom,
  moveRack,
  occupantOf,
  RACK_DEPTH,
  RACK_WIDTH,
  rowBands,
  STATUS_COLOR,
  worstStatus,
  type Cell,
  type PlacedRack,
} from './scene3d';

interface DataCenter3DProps {
  racks: RackItem[];
  /** Con un solo gabinete es la vista de gabinete (W-44): más cerca y con los nombres de los equipos. */
  single?: boolean;
  onOpenRack?: (id: number) => void;
  onOpenDevice: (id: number) => void;
  /** Con permiso de edición: arrastrar los gabinetes por el piso guarda el plano (decisión 96). */
  onMoveRacks?: (changes: RackPlacementInput[]) => void;
}

/** ¿Hay WebGL? (en pruebas o en equipos sin aceleración, se muestra un aviso en vez de la escena). */
function hasWebGl(): boolean {
  try {
    const canvas = document.createElement('canvas');
    return !!(canvas.getContext('webgl2') ?? canvas.getContext('webgl'));
  } catch {
    return false;
  }
}

/**
 * W-43 Vista 3D de la sala y W-44 gabinete 3D: los gabinetes en su fila y posición del plano, y cada equipo a su
 * altura real y del color de su estado. Arrastrar gira la vista, la rueda acerca; un clic abre el equipo.
 */
export default function DataCenter3D(props: DataCenter3DProps) {
  const api = usePanelApi();
  const details = useQueries({
    queries: props.racks.map((r) => ({ queryKey: ['red', 'gabinete', r.id], queryFn: () => api.red.rack(r.id) })),
  });
  return <DataCenter3DScene {...props} details={details.flatMap((d) => (d.data ? [d.data] : []))} />;
}

/** Vista de la cámara: libre (girar a mano), desde arriba o de frente a una fila. */
type View = 'libre' | 'arriba' | `fila:${string}`;

/** Dónde corta el rayo del mouse al piso (y = 0); null si mira hacia arriba. */
function floorPoint(e: ThreeEvent<PointerEvent>): { x: number; z: number } | null {
  const { origin, direction } = e.ray;
  if (direction.y >= -1e-6) return null;
  const t = -origin.y / direction.y;
  return { x: origin.x + t * direction.x, z: origin.z + t * direction.z };
}

/** La escena con los datos ya cargados: la usan el panel y la ventana 3D aparte (7.6), que recibe los datos del panel. */
export function DataCenter3DScene({
  racks,
  details,
  single = false,
  onOpenRack,
  onOpenDevice,
  onMoveRacks,
}: DataCenter3DProps & { details: RackDetail[] }) {
  const placed = useMemo(() => layoutRoom(racks), [racks]);
  const bands = useMemo(() => (single ? [] : rowBands(racks)), [racks, single]);
  const [hovered, setHovered] = useState<number | null>(null);
  const [view, setView] = useState<View>('libre');
  const [onlyProblems, setOnlyProblems] = useState(false);
  const [moving, setMoving] = useState(false);
  const [drag, setDrag] = useState<{ id: number; cell: Cell | null } | null>(null);
  const webgl = useMemo(() => hasWebGl(), []);
  const { width, depth, center, distance } = useMemo(() => {
    const w = Math.max(...placed.map((p) => p.x), 0) + RACK_WIDTH;
    const d = Math.max(...placed.map((p) => p.z), 0) + RACK_DEPTH;
    return {
      width: w,
      depth: d,
      center: [w / 2 - RACK_WIDTH / 2, 0.9, d / 2 - RACK_DEPTH / 2] as [number, number, number],
      distance: single ? 2.6 : Math.max(4, Math.max(w, d) * 1.4),
    };
  }, [placed, single]);

  if (!webgl)
    return (
      <p className="muted dc-3d-fallback" role="status">
        Este equipo no tiene aceleración gráfica (WebGL): use el plano o el gabinete en 2D.
      </p>
    );

  const byId = new Map(details.map((d) => [d.rack.id, d]));
  const canMove = Boolean(onMoveRacks) && !single;

  // Arrastrar un gabinete: se sigue el punto del piso bajo el mouse y se muestra dónde va a quedar.
  const startDrag = (id: number) => (e: ThreeEvent<PointerEvent>) => {
    if (!moving) return;
    e.stopPropagation();
    (e.target as Element).setPointerCapture?.(e.pointerId);
    setDrag({ id, cell: null });
  };
  const moveDrag = (e: ThreeEvent<PointerEvent>) => {
    if (!drag) return;
    const point = floorPoint(e);
    if (point) setDrag({ id: drag.id, cell: cellAt(racks, point.x, point.z) });
  };
  const endDrag = (e: ThreeEvent<PointerEvent>) => {
    if (!drag) return;
    (e.target as Element).releasePointerCapture?.(e.pointerId);
    const changes = drag.cell ? moveRack(racks, drag.id, drag.cell) : null;
    setDrag(null);
    if (changes && changes.length > 0) onMoveRacks?.(changes);
  };

  const ghost = drag?.cell ? cellCenter(racks, drag.cell) : null;
  const swapping = drag?.cell ? occupantOf(racks, drag.cell) : null;

  return (
    <div className="dc-3d" aria-label={single ? `Gabinete ${racks[0]?.name ?? ''} en 3D` : 'Sala en 3D'}>
      {!single && (
        <div className="dc-3d-tools" role="toolbar" aria-label="Vista 3D">
          <div className="segmented" role="group" aria-label="Vista">
            <button type="button" aria-pressed={view === 'libre'} onClick={() => setView('libre')}>
              Libre
            </button>
            <button type="button" aria-pressed={view === 'arriba'} onClick={() => setView('arriba')}>
              Desde arriba
            </button>
            {bands.map((b) => (
              <button
                key={b.name}
                type="button"
                aria-pressed={view === `fila:${b.name}`}
                onClick={() => setView(`fila:${b.name}`)}
              >
                Fila {b.name}
              </button>
            ))}
          </div>
          <label className="dc-3d-check">
            <input type="checkbox" checked={onlyProblems} onChange={(e) => setOnlyProblems(e.target.checked)} />
            Solo problemas
          </label>
          {canMove && (
            <button
              type="button"
              className={moving ? 'btn btn-primary' : 'btn btn-outline'}
              aria-pressed={moving}
              onClick={() => {
                setMoving(!moving);
                setDrag(null);
              }}
            >
              {moving ? 'Listo' : 'Mover gabinetes'}
            </button>
          )}
        </div>
      )}
      <Canvas camera={{ position: [center[0] + distance * 0.6, 1.8 + distance * 0.35, center[2] + distance], fov: 45 }}>
        <color attach="background" args={['#f3f0e8']} />
        <ambientLight intensity={0.7} />
        <directionalLight position={[4, 6, 5]} intensity={0.9} />
        <gridHelper
          args={[
            Math.ceil(Math.max(width, depth) + 4),
            Math.ceil(Math.max(width, depth) + 4) * 2,
            '#c9c3b6',
            '#e3ded3',
          ]}
          position={[center[0], 0, center[2]]}
        />
        {/* El nombre de cada fila en el piso, al principio de la fila. */}
        {bands.map((b) => (
          <Html
            key={b.name}
            position={[b.from - RACK_WIDTH / 2 - 0.45, 0.02, b.z]}
            center
            className="dc-3d-row-label"
            zIndexRange={[4, 0]}
          >
            <span className="mono">FILA {b.name}</span>
          </Html>
        ))}
        {placed.map((p) => (
          <RackModel
            key={p.id}
            placed={p}
            detail={byId.get(p.id)}
            single={single}
            hovered={hovered}
            onlyProblems={onlyProblems}
            moving={moving}
            dragging={drag?.id === p.id}
            onHover={setHovered}
            onOpenRack={onOpenRack}
            onOpenDevice={onOpenDevice}
            onPointerDown={startDrag(p.id)}
            onPointerMove={moveDrag}
            onPointerUp={endDrag}
          />
        ))}
        {ghost && (
          <mesh position={[ghost.x, 0.01, ghost.z]} rotation={[-Math.PI / 2, 0, 0]}>
            <planeGeometry args={[RACK_WIDTH, RACK_DEPTH]} />
            <meshBasicMaterial
              color={swapping && swapping !== drag?.id ? '#e8971a' : '#2f9e6f'}
              transparent
              opacity={0.55}
            />
          </mesh>
        )}
        <OrbitControls
          makeDefault
          enabled={!drag}
          target={center}
          maxPolarAngle={Math.PI / 2.05}
          minDistance={1}
          maxDistance={40}
        />
        <CameraView view={view} center={center} distance={distance} bands={bands} />
      </Canvas>
      <p className="muted dc-3d-help">
        {moving
          ? 'Arrastre un gabinete a otro lugar del piso: verde = lugar libre, naranja = se intercambian. Pulse «Listo» al terminar.'
          : 'Arrastre para girar · rueda para acercar · clic en un equipo para abrirlo'}
      </p>
    </div>
  );
}

/** Lleva la cámara a la vista elegida (la vista libre la deja donde la puso el técnico). */
function CameraView({
  view,
  center,
  distance,
  bands,
}: {
  view: View;
  center: [number, number, number];
  distance: number;
  bands: ReturnType<typeof rowBands>;
}) {
  const { camera, controls } = useThree();
  const first = useRef(true);
  useEffect(() => {
    if (first.current) {
      first.current = false;
      return;
    }
    const orbit = controls as unknown as {
      target: { set: (x: number, y: number, z: number) => void };
      update: () => void;
    } | null;
    let target: [number, number, number] = center;
    if (view === 'libre') camera.position.set(center[0] + distance * 0.6, 1.8 + distance * 0.35, center[2] + distance);
    else if (view === 'arriba') camera.position.set(center[0], distance * 1.3, center[2] + 0.01);
    else {
      const band = bands.find((b) => `fila:${b.name}` === view);
      if (!band) return;
      const middle = (band.from + band.to) / 2;
      target = [middle, 1, band.z];
      // De frente al lado por donde se ven los equipos (el pasillo frío).
      camera.position.set(middle, 1.4, band.z + band.facing * Math.max(2.5, (band.to - band.from) * 0.9));
    }
    if (orbit) {
      orbit.target.set(...target);
      orbit.update();
    } else camera.lookAt(...target);
    // Solo al cambiar de vista: los datos que se actualizan solos no mueven la cámara.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view]);
  return null;
}

function RackModel({
  placed,
  detail,
  single,
  hovered,
  onlyProblems,
  moving,
  dragging,
  onHover,
  onOpenRack,
  onOpenDevice,
  onPointerDown,
  onPointerMove,
  onPointerUp,
}: {
  placed: PlacedRack;
  detail: RackDetail | undefined;
  single: boolean;
  hovered: number | null;
  onlyProblems: boolean;
  moving: boolean;
  dragging: boolean;
  onHover: (id: number | null) => void;
  onOpenRack?: (id: number) => void;
  onOpenDevice: (id: number) => void;
  onPointerDown: (e: ThreeEvent<PointerEvent>) => void;
  onPointerMove: (e: ThreeEvent<PointerEvent>) => void;
  onPointerUp: (e: ThreeEvent<PointerEvent>) => void;
}) {
  const devices = detail ? deviceBoxes(detail.devices) : [];
  const worst = detail ? worstStatus(detail.devices) : null;
  const problem = (id: number) => {
    const d = detail?.devices.find((x) => x.id === id);
    return Boolean(d && (d.portsWithProblems > 0 || !['en_linea', 'pasivo'].includes(d.status)));
  };

  return (
    <group position={[placed.x, 0, placed.z]} rotation={[0, placed.rotation, 0]}>
      {/* Gabinete: vidrio y aristas, del color de su peor equipo. Al moverlo, se agarra de aquí. */}
      <mesh
        position={[0, placed.height / 2, 0]}
        onClick={(e) => {
          e.stopPropagation();
          if (!moving) onOpenRack?.(placed.id);
        }}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
      >
        <boxGeometry args={[RACK_WIDTH, placed.height, RACK_DEPTH]} />
        <meshStandardMaterial
          color={moving ? '#2c8f86' : '#20313a'}
          transparent
          opacity={dragging ? 0.35 : moving ? 0.2 : 0.12}
        />
        <Edges color={worst ? STATUS_COLOR[worst] : '#5b6770'} />
      </mesh>
      {devices.map((d) => {
        const dim = onlyProblems && !problem(d.id);
        return (
          <mesh
            key={d.id}
            position={[0, d.y, 0.02]}
            onClick={(e) => {
              e.stopPropagation();
              if (!moving) onOpenDevice(d.id);
            }}
            onPointerOver={(e) => (e.stopPropagation(), onHover(d.id))}
            onPointerOut={() => onHover(null)}
            onPointerDown={moving ? onPointerDown : undefined}
            onPointerMove={moving ? onPointerMove : undefined}
            onPointerUp={moving ? onPointerUp : undefined}
          >
            <boxGeometry args={[RACK_WIDTH - 0.08, d.height, RACK_DEPTH - 0.12]} />
            <meshStandardMaterial
              color={dim ? '#b9bec2' : d.color}
              transparent={dim}
              opacity={dim ? 0.25 : 1}
              emissive={hovered === d.id ? d.color : '#000000'}
              emissiveIntensity={0.35}
            />
            {(single || hovered === d.id) && (
              <Html position={[RACK_WIDTH / 2, 0, RACK_DEPTH / 2]} className="dc-3d-label" zIndexRange={[10, 0]}>
                <span className="mono">{d.name}</span>
                {hovered === d.id && detail && (
                  <span>{DEVICE_STATUS_LABEL[detail.devices.find((x) => x.id === d.id)!.status]}</span>
                )}
              </Html>
            )}
          </mesh>
        );
      })}
      <Html position={[0, placed.height + 0.12, 0]} center className="dc-3d-rack-label" zIndexRange={[5, 0]}>
        <span className="mono">{placed.name}</span>
      </Html>
    </group>
  );
}

import { useMemo, useState } from 'react';
import { useQueries } from '@tanstack/react-query';
import { Canvas } from '@react-three/fiber';
import { Edges, Html, OrbitControls } from '@react-three/drei';
import type { RackDetail, RackItem } from '@monitoreoti/shared';
import { usePanelApi } from '../../data/PanelData';
import { DEVICE_STATUS_LABEL } from './netLabels';
import { deviceBoxes, layoutRoom, RACK_DEPTH, RACK_WIDTH, STATUS_COLOR, worstStatus, type PlacedRack } from './scene3d';

interface DataCenter3DProps {
  racks: RackItem[];
  /** Con un solo gabinete es la vista de gabinete (W-44): más cerca y con los nombres de los equipos. */
  single?: boolean;
  onOpenRack?: (id: number) => void;
  onOpenDevice: (id: number) => void;
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

/** La escena con los datos ya cargados: la usan el panel y la ventana 3D aparte (7.6), que recibe los datos del panel. */
export function DataCenter3DScene({
  racks,
  details,
  single = false,
  onOpenRack,
  onOpenDevice,
}: DataCenter3DProps & { details: RackDetail[] }) {
  const placed = useMemo(() => layoutRoom(racks), [racks]);
  const [hovered, setHovered] = useState<number | null>(null);
  const webgl = useMemo(() => hasWebGl(), []);

  if (!webgl)
    return (
      <p className="muted dc-3d-fallback" role="status">
        Este equipo no tiene aceleración gráfica (WebGL): use el plano o el gabinete en 2D.
      </p>
    );

  const width = Math.max(...placed.map((p) => p.x), 0) + RACK_WIDTH;
  const depth = Math.max(...placed.map((p) => p.z), 0) + RACK_DEPTH;
  const center: [number, number, number] = [width / 2 - RACK_WIDTH / 2, 0.9, depth / 2 - RACK_DEPTH / 2];
  const distance = single ? 2.6 : Math.max(4, Math.max(width, depth) * 1.4);
  const byId = new Map(details.map((d) => [d.rack.id, d]));

  return (
    <div className="dc-3d" aria-label={single ? `Gabinete ${racks[0]?.name ?? ''} en 3D` : 'Sala en 3D'}>
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
        {placed.map((p) => (
          <RackModel
            key={p.id}
            placed={p}
            detail={byId.get(p.id)}
            single={single}
            hovered={hovered}
            onHover={setHovered}
            onOpenRack={onOpenRack}
            onOpenDevice={onOpenDevice}
          />
        ))}
        <OrbitControls target={center} maxPolarAngle={Math.PI / 2.05} minDistance={1} maxDistance={40} />
      </Canvas>
      <p className="muted dc-3d-help">Arrastre para girar · rueda para acercar · clic en un equipo para abrirlo</p>
    </div>
  );
}

function RackModel({
  placed,
  detail,
  single,
  hovered,
  onHover,
  onOpenRack,
  onOpenDevice,
}: {
  placed: PlacedRack;
  detail: RackDetail | undefined;
  single: boolean;
  hovered: number | null;
  onHover: (id: number | null) => void;
  onOpenRack?: (id: number) => void;
  onOpenDevice: (id: number) => void;
}) {
  const devices = detail ? deviceBoxes(detail.devices) : [];
  const worst = detail ? worstStatus(detail.devices) : null;

  return (
    <group position={[placed.x, 0, placed.z]}>
      {/* Gabinete: vidrio y aristas, del color de su peor equipo. */}
      <mesh position={[0, placed.height / 2, 0]} onClick={(e) => (e.stopPropagation(), onOpenRack?.(placed.id))}>
        <boxGeometry args={[RACK_WIDTH, placed.height, RACK_DEPTH]} />
        <meshStandardMaterial color="#20313a" transparent opacity={0.12} />
        <Edges color={worst ? STATUS_COLOR[worst] : '#5b6770'} />
      </mesh>
      {devices.map((d) => (
        <mesh
          key={d.id}
          position={[0, d.y, 0.02]}
          onClick={(e) => (e.stopPropagation(), onOpenDevice(d.id))}
          onPointerOver={(e) => (e.stopPropagation(), onHover(d.id))}
          onPointerOut={() => onHover(null)}
        >
          <boxGeometry args={[RACK_WIDTH - 0.08, d.height, RACK_DEPTH - 0.12]} />
          <meshStandardMaterial
            color={d.color}
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
      ))}
      <Html position={[0, placed.height + 0.12, 0]} center className="dc-3d-rack-label" zIndexRange={[5, 0]}>
        <span className="mono">{placed.name}</span>
      </Html>
    </group>
  );
}

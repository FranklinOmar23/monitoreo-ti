import { lazy, Suspense, useState, type DragEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DeviceListItem, RackDevice, RackItem, RackPlacementInput, RoomItem } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { StatusBadge } from '@components/StatusBadge';
import { usePanelApi } from '../../data/PanelData';
import { DeviceForm, RackForm, RoomForm } from './NetworkForms';
import { DEVICE_STATUS_LABEL, DEVICE_STATUS_TONE, DEVICE_TYPE_LABEL } from './netLabels';
import { moveRack, nextRowName, planRows, type Cell } from './scene3d';
import { useOpenView3D } from './View3DWindows';
import { count } from '../../data/format';

// three.js pesa: se carga solo al abrir una vista 3D.
const DataCenter3D = lazy(() => import('./DataCenter3D'));

type RoomMode = 'gabinetes' | 'plano' | '3d';

interface DataCenterViewProps {
  canEdit: boolean;
  onOpenDevice: (id: number) => void;
}

/** W-58 Salas → gabinetes de la sala → W-45 gabinete 2D con su editor (W-60). */
export function DataCenterView({ canEdit, onOpenDevice }: DataCenterViewProps) {
  const api = usePanelApi();
  const rooms = useQuery({ queryKey: ['red', 'salas'], queryFn: () => api.red.rooms() });
  const [roomId, setRoomId] = useState<number | null>(null);
  const [rackId, setRackId] = useState<number | null>(null);
  const [editing, setEditing] = useState<
    { kind: 'sala'; room?: RoomItem } | { kind: 'gabinete'; rack?: RackItem } | null
  >(null);

  if (rooms.isPending) return <Loading variant="cards" />;
  if (rooms.isError) return <ErrorState error={rooms.error} onRetry={() => void rooms.refetch()} />;

  const room = rooms.data.find((r) => r.id === roomId) ?? rooms.data[0];

  return (
    <div className="dc-layout">
      <aside className="dc-rooms">
        <div className="dc-rooms-head">
          <strong>Salas</strong>
          {canEdit && (
            <button type="button" className="link-quiet" onClick={() => setEditing({ kind: 'sala' })}>
              <Icon name="plus" size={14} /> Agregar
            </button>
          )}
        </div>
        {rooms.data.length === 0 ? (
          <p className="muted">Todavía no hay salas.</p>
        ) : (
          <ul>
            {rooms.data.map((r) => (
              <li key={r.id}>
                <button
                  type="button"
                  className="dc-room"
                  aria-current={r.id === room?.id ? 'true' : undefined}
                  onClick={() => {
                    setRoomId(r.id);
                    setRackId(null);
                  }}
                >
                  <strong>{r.name}</strong>
                  <span className="muted">
                    {r.branchName} · {count(r.rackCount, 'gabinete')} · {count(r.deviceCount, 'equipo')}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </aside>

      <div className="dc-main">
        {!room ? (
          <EmptyState title="Agregue la primera sala">
            Una sala o cuarto de comunicaciones agrupa los gabinetes de una sucursal.
          </EmptyState>
        ) : rackId !== null ? (
          <RackView rackId={rackId} canEdit={canEdit} onBack={() => setRackId(null)} onOpenDevice={onOpenDevice} />
        ) : (
          <RoomRacks
            room={room}
            canEdit={canEdit}
            onOpenRack={setRackId}
            onOpenDevice={onOpenDevice}
            onEditRoom={() => setEditing({ kind: 'sala', room })}
            onAddRack={() => setEditing({ kind: 'gabinete' })}
          />
        )}
      </div>

      {editing?.kind === 'sala' && <RoomForm room={editing.room} onClose={() => setEditing(null)} />}
      {editing?.kind === 'gabinete' && (
        <RackForm rack={editing.rack} roomId={room?.id} onClose={() => setEditing(null)} />
      )}
    </div>
  );
}

function RoomRacks({
  room,
  canEdit,
  onOpenRack,
  onOpenDevice,
  onEditRoom,
  onAddRack,
}: {
  room: RoomItem;
  canEdit: boolean;
  onOpenRack: (id: number) => void;
  onOpenDevice: (id: number) => void;
  onEditRoom: () => void;
  onAddRack: () => void;
}) {
  const api = usePanelApi();
  const racks = useQuery({ queryKey: ['red', 'gabinetes', room.id], queryFn: () => api.red.racks(room.id) });
  const [mode, setMode] = useState<RoomMode>('gabinetes');
  const queries = useQueryClient();
  // Plano de la sala (decisión 96): lo que se arrastra en el plano o en el 3D se guarda de una vez.
  const layout = useMutation({
    mutationFn: (changes: RackPlacementInput[]) => api.red.saveRoomLayout(room.id, changes),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['red'] }),
  });
  const openWindow = useOpenView3D();

  return (
    <Card
      title={room.name}
      aside={
        canEdit && (
          <div className="ficha-actions">
            <button type="button" className="btn btn-outline" onClick={onEditRoom}>
              Editar sala
            </button>
            <button type="button" className="btn btn-primary" onClick={onAddRack}>
              Agregar gabinete
            </button>
          </div>
        )
      }
    >
      {layout.isError && (
        <p className="auth-error" role="alert">
          {layout.error.message}
        </p>
      )}
      <div className="dc-room-bar">
        <p className="muted">
          {room.branchName}
          {room.location && ` · ${room.location}`}
        </p>
        <div className="segmented segmented-lg" role="group" aria-label="Cómo ver la sala">
          {(
            [
              ['gabinetes', 'Gabinetes'],
              ['plano', 'Plano'],
              ['3d', '3D'],
            ] as const
          ).map(([value, label]) => (
            <button key={value} type="button" aria-pressed={mode === value} onClick={() => setMode(value)}>
              {label}
            </button>
          ))}
        </div>
        {openWindow && (
          <button
            type="button"
            className="btn btn-outline"
            title="Abrir la sala en 3D en su propia ventana (por ejemplo, en otro monitor)"
            onClick={() => openWindow({ kind: 'sala', id: room.id, title: room.name })}
          >
            <Icon name="expand" size={16} /> 3D en ventana
          </button>
        )}
      </div>
      {racks.isPending ? (
        <Loading />
      ) : racks.isError ? (
        <ErrorState error={racks.error} />
      ) : racks.data.length === 0 ? (
        <EmptyState title="La sala no tiene gabinetes" />
      ) : mode === 'plano' ? (
        <FloorPlan
          racks={racks.data}
          canEdit={canEdit}
          saving={layout.isPending}
          onOpenRack={onOpenRack}
          onMove={(changes) => layout.mutate(changes)}
        />
      ) : mode === '3d' ? (
        <Suspense fallback={<Loading label="Cargando la vista 3D…" />}>
          <DataCenter3D
            racks={racks.data}
            onOpenRack={onOpenRack}
            onOpenDevice={onOpenDevice}
            onMoveRacks={canEdit ? (changes) => layout.mutate(changes) : undefined}
          />
        </Suspense>
      ) : (
        <div className="rack-grid">
          {racks.data.map((rack) => (
            <button key={rack.id} type="button" className="rack-card" onClick={() => onOpenRack(rack.id)}>
              <span className="rack-card-name mono">{rack.name}</span>
              <span className="muted">
                {rack.row
                  ? `Fila ${rack.row}${rack.position ? ` · posición ${rack.position}` : ''}`
                  : 'Sin ubicación en el plano'}
              </span>
              <span className="rack-usage" aria-label={`${rack.usedUnits} de ${rack.heightUnits} U ocupadas`}>
                <span style={{ width: `${Math.min(100, (100 * rack.usedUnits) / rack.heightUnits)}%` }} />
              </span>
              <span className="mono muted">
                {rack.usedUnits}/{rack.heightUnits} U · {count(rack.deviceCount, 'equipo')}
                {!rack.isActive && ' · inactivo'}
              </span>
            </button>
          ))}
        </div>
      )}
    </Card>
  );
}

/**
 * W-59 Plano de la sala: los gabinetes por fila y posición, vistos desde arriba. Con «Editar plano» (decisión 96) se
 * arrastran a otro lugar (sobre otro, se intercambian), a una fila nueva o a «Sin ubicar»; cada movimiento se guarda.
 */
function FloorPlan({
  racks,
  canEdit,
  saving,
  onOpenRack,
  onMove,
}: {
  racks: RackItem[];
  canEdit: boolean;
  saving: boolean;
  onOpenRack: (id: number) => void;
  onMove: (changes: RackPlacementInput[]) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [extraRows, setExtraRows] = useState<string[]>([]);
  const [over, setOver] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const rows = planRows(racks);
  const named = rows.filter((r) => r.row !== null);
  const unplaced = rows.find((r) => r.row === null)?.racks ?? [];
  const names = [...named.map((r) => r.row!), ...extraRows.filter((n) => !named.some((r) => r.row === n))];
  const lastColumn = Math.max(...named.flatMap((r) => r.racks.map((x) => x.column)), 0);
  const columns = editing ? Math.max(lastColumn + 1, 4) : Math.max(lastColumn, 1);
  const byId = new Map(racks.map((r) => [r.id, r]));

  const drop = (cell: Cell | null) => (event: DragEvent) => {
    event.preventDefault();
    setOver(null);
    const id = Number(event.dataTransfer.getData('text/plain'));
    if (!id) return;
    const changes = moveRack(racks, id, cell);
    if (changes === null) setNotice('Desde «Sin ubicar», suelte el gabinete en un lugar libre.');
    else if (changes.length > 0) {
      setNotice(null);
      onMove(changes);
    }
  };
  const target = (key: string, cell: Cell | null) =>
    editing
      ? {
          'data-over': over === key || undefined,
          onDragOver: (e: DragEvent) => {
            e.preventDefault();
            setOver(key);
          },
          onDragLeave: () => setOver(null),
          onDrop: drop(cell),
        }
      : {};

  const rackButton = (id: number) => {
    const full = byId.get(id)!;
    return (
      <button
        key={id}
        type="button"
        className="floor-rack"
        title={editing ? 'Arrástrelo a otro lugar' : `${full.usedUnits} de ${full.heightUnits} U ocupadas`}
        draggable={editing}
        data-editing={editing || undefined}
        onDragStart={(e) => {
          e.dataTransfer.setData('text/plain', String(id));
          e.dataTransfer.effectAllowed = 'move';
        }}
        onClick={() => !editing && onOpenRack(id)}
      >
        <span className="mono">{full.name}</span>
        <span className="rack-usage" aria-hidden="true">
          <span style={{ width: `${Math.min(100, (100 * full.usedUnits) / full.heightUnits)}%` }} />
        </span>
        <span className="muted mono">{count(full.deviceCount, 'equipo')}</span>
      </button>
    );
  };

  return (
    <div className="floor-plan" role="list" aria-label="Plano de la sala" data-editing={editing || undefined}>
      {canEdit && (
        <div className="floor-plan-bar">
          {editing && (
            <span className="muted">
              Arrastre los gabinetes: sobre otro se intercambian. Cada cambio se guarda solo
              {saving ? ' · guardando…' : '.'}
            </span>
          )}
          <span className="floor-plan-spacer" />
          {editing && (
            <button
              type="button"
              className="btn btn-outline"
              onClick={() => setExtraRows([...extraRows, nextRowName(names)])}
            >
              <Icon name="plus" size={14} /> Agregar fila
            </button>
          )}
          <button
            type="button"
            className={editing ? 'btn btn-primary' : 'btn btn-outline'}
            onClick={() => {
              setEditing(!editing);
              setExtraRows([]);
              setNotice(null);
            }}
          >
            {editing ? 'Listo' : 'Editar plano'}
          </button>
        </div>
      )}
      {notice && (
        <p className="auth-error" role="alert">
          {notice}
        </p>
      )}
      {names.map((name, index) => {
        const row = named.find((r) => r.row === name);
        return (
          <div key={name} className="floor-row" role="listitem">
            <span className="floor-row-name">
              Fila {name}
              <span className="floor-row-facing muted" title="Hacia dónde mira el frente de los gabinetes">
                frente {index % 2 === 0 ? '↓' : '↑'}
              </span>
            </span>
            <div className="floor-row-racks" style={{ gridTemplateColumns: `repeat(${columns}, minmax(84px, 1fr))` }}>
              {row?.racks.map(({ rack, column }) => (
                <div
                  key={rack.id}
                  className="floor-cell"
                  style={{ gridColumn: column }}
                  {...target(`${name}|${column}`, { row: name, position: column })}
                >
                  {rackButton(rack.id)}
                </div>
              ))}
              {editing &&
                Array.from({ length: columns }, (_, i) => i + 1)
                  .filter((c) => !row?.racks.some((r) => r.column === c))
                  .map((c) => (
                    <div
                      key={`libre-${c}`}
                      className="floor-cell floor-cell-free"
                      style={{ gridColumn: c }}
                      aria-label={`Fila ${name}, posición ${c}: libre`}
                      {...target(`${name}|${c}`, { row: name, position: c })}
                    />
                  ))}
            </div>
          </div>
        );
      })}
      {(unplaced.length > 0 || editing) && (
        <div className="floor-row floor-row-unplaced" role="listitem" {...target('sin-ubicar', null)}>
          <span className="floor-row-name">Sin ubicar</span>
          <div className="floor-row-racks floor-tray">
            {unplaced.length === 0 ? (
              <span className="muted">Suelte aquí un gabinete para sacarlo del plano.</span>
            ) : (
              unplaced.map(({ rack }) => rackButton(rack.id))
            )}
          </div>
        </div>
      )}
      <p className="muted note">
        Las filas se miran de a pares (A con B, C con D): el frente de los gabinetes da al pasillo frío entre ellas.
      </p>
    </div>
  );
}

/** La U más baja donde entra un equipo de esa altura; null si no hay lugar. */
export function firstFreeU(
  rackHeight: number,
  devices: readonly Pick<RackDevice, 'positionU' | 'heightUnits'>[],
  height: number,
): number | null {
  const size = Math.max(height, 1);
  for (let u = 1; u + size - 1 <= rackHeight; u++) {
    const clash = devices.some((d) => u < d.positionU + d.heightUnits && d.positionU < u + size);
    if (!clash) return u;
  }
  return null;
}

/** Equipos de la sucursal sin gabinete: se arrastran a una U libre o se ponen en la primera donde entran. */
function LooseDevices({
  devices,
  pending,
  firstFree,
  onPlace,
}: {
  devices: DeviceListItem[];
  pending: boolean;
  firstFree: (height: number) => number | null;
  onPlace: (id: number, u: number) => void;
}) {
  return (
    <Card title="Sin gabinete" aside={<span className="eyebrow mono">{devices.length}</span>}>
      {pending ? (
        <Loading variant="list" rows={3} />
      ) : devices.length === 0 ? (
        <p className="muted">Todos los equipos de la sucursal están en un gabinete.</p>
      ) : (
        <ul className="component-list loose-list" aria-label="Equipos sin gabinete">
          {devices.map((d) => {
            const u = firstFree(d.heightUnits);
            return (
              <li
                key={d.id}
                draggable
                className="loose-device"
                title="Arrástrelo a una U libre del gabinete"
                onDragStart={(e) => {
                  e.dataTransfer.setData('text/plain', String(d.id));
                  e.dataTransfer.effectAllowed = 'move';
                }}
              >
                <div className="component-text">
                  <span className="mono">{d.name}</span>
                  <span>
                    {DEVICE_TYPE_LABEL[d.type]} · {d.modelName} · {d.heightUnits} U
                  </span>
                </div>
                <button
                  type="button"
                  className="btn btn-outline btn-sm"
                  disabled={u === null}
                  title={u === null ? 'No hay lugar libre para su altura' : `Ponerlo en la U ${u}`}
                  onClick={() => u !== null && onPlace(d.id, u)}
                >
                  {u === null ? 'Sin lugar' : `Poner en U${u}`}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </Card>
  );
}

/** Altura de una U en el dibujo del gabinete. */
const U_PX = 22;

function RackView({
  rackId,
  canEdit,
  onBack,
  onOpenDevice,
}: {
  rackId: number;
  canEdit: boolean;
  onBack: () => void;
  onOpenDevice: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const detail = useQuery({ queryKey: ['red', 'gabinete', rackId], queryFn: () => api.red.rack(rackId) });
  const [editingRack, setEditingRack] = useState(false);
  const [adding, setAdding] = useState<number | null>(null);
  const [dropU, setDropU] = useState<number | null>(null);
  const [in3d, setIn3d] = useState(false);
  const openWindow = useOpenView3D();

  const move = useMutation({
    mutationFn: ({ id, u }: { id: number; u: number }) => api.red.placeDevice(id, rackId, u),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['red'] }),
  });
  // Equipos de la sucursal que no están en ningún gabinete (por ejemplo, los FortiSwitch que trajo el FortiGate):
  // se arrastran a una U libre o van a la primera donde entran (decisión 96).
  const branchId = detail.data?.rack.branchId;
  const loose = useQuery({
    queryKey: ['red', 'dispositivos', 'sin-gabinete', branchId],
    queryFn: () => api.red.devices({ sucursal: branchId }),
    enabled: canEdit && branchId !== undefined,
    select: (list) => list.filter((d) => d.rackId === null && d.status !== 'baja'),
  });

  if (detail.isPending) return <Loading />;
  if (detail.isError) return <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />;
  const { rack, devices } = detail.data;

  const occupant = (u: number) => devices.find((d) => u >= d.positionU && u < d.positionU + d.heightUnits);

  const drop = (u: number) => (event: DragEvent) => {
    event.preventDefault();
    setDropU(null);
    const id = Number(event.dataTransfer.getData('text/plain'));
    if (id) move.mutate({ id, u });
  };

  return (
    <>
      <nav className="breadcrumb" aria-label="Ruta">
        <button type="button" className="link-quiet" onClick={onBack}>
          {rack.roomName}
        </button>
        <span aria-hidden="true">›</span>
        <strong className="mono">{rack.name}</strong>
      </nav>
      <div className="ficha-grid">
        <Card
          title={`${rack.name} · ${rack.heightUnits} U`}
          aside={
            <div className="ficha-actions">
              <div className="segmented segmented-lg" role="group" aria-label="Cómo ver el gabinete">
                <button type="button" aria-pressed={!in3d} onClick={() => setIn3d(false)}>
                  2D
                </button>
                <button type="button" aria-pressed={in3d} onClick={() => setIn3d(true)}>
                  3D
                </button>
              </div>
              {openWindow && (
                <button
                  type="button"
                  className="btn btn-outline"
                  title="Abrir el gabinete en 3D en su propia ventana"
                  onClick={() =>
                    openWindow({ kind: 'gabinete', id: rack.id, title: `${rack.roomName} · ${rack.name}` })
                  }
                >
                  <Icon name="expand" size={16} /> 3D en ventana
                </button>
              )}
              {canEdit && (
                <button type="button" className="btn btn-outline" onClick={() => setEditingRack(true)}>
                  Editar gabinete
                </button>
              )}
            </div>
          }
        >
          {move.isError && (
            <p className="auth-error" role="alert">
              {move.error.message}
            </p>
          )}
          {in3d ? (
            <Suspense fallback={<Loading label="Cargando la vista 3D…" />}>
              <DataCenter3D racks={[rack]} single onOpenDevice={onOpenDevice} />
            </Suspense>
          ) : (
            <>
              {canEdit && (
                <p className="muted note">
                  Arrastre un equipo a otra U para moverlo, o traiga uno de «Sin gabinete». Clic en una U libre para
                  registrar uno nuevo.
                </p>
              )}
              <div
                className="rack"
                style={{ gridTemplateRows: `repeat(${rack.heightUnits}, ${U_PX}px)` }}
                role="list"
                aria-label={`Gabinete ${rack.name}`}
              >
                {Array.from({ length: rack.heightUnits }, (_, i) => rack.heightUnits - i).map((u) => {
                  const device = occupant(u);
                  const row = rack.heightUnits - u + 1;
                  if (device && u !== device.positionU + device.heightUnits - 1) return null;
                  return device ? (
                    <RackBlock
                      key={`d${device.id}`}
                      device={device}
                      row={row}
                      draggable={canEdit}
                      onOpen={() => onOpenDevice(device.id)}
                    />
                  ) : (
                    <div
                      key={`u${u}`}
                      role="listitem"
                      className="rack-slot"
                      data-over={dropU === u || undefined}
                      style={{ gridRow: row }}
                      onDragOver={
                        canEdit
                          ? (e) => {
                              e.preventDefault();
                              setDropU(u);
                            }
                          : undefined
                      }
                      onDragLeave={() => setDropU(null)}
                      onDrop={canEdit ? drop(u) : undefined}
                    >
                      <span className="rack-u mono">{u}</span>
                      {canEdit && (
                        <button
                          type="button"
                          className="rack-add"
                          aria-label={`Agregar equipo en la U ${u}`}
                          onClick={() => setAdding(u)}
                        >
                          +
                        </button>
                      )}
                    </div>
                  );
                })}
              </div>
            </>
          )}
        </Card>

        {canEdit && (
          <LooseDevices
            devices={loose.data ?? []}
            pending={loose.isPending}
            firstFree={(height) => firstFreeU(rack.heightUnits, devices, height)}
            onPlace={(id, u) => move.mutate({ id, u })}
          />
        )}

        <Card title="Equipos del gabinete">
          {devices.length === 0 ? (
            <p className="muted">El gabinete está vacío.</p>
          ) : (
            <ul className="component-list">
              {devices.map((d) => (
                <li key={d.id}>
                  <span className="mono muted rack-list-u">U{d.positionU}</span>
                  <div className="component-text">
                    <button type="button" className="row-name mono" onClick={() => onOpenDevice(d.id)}>
                      {d.name}
                    </button>
                    <span>
                      {DEVICE_TYPE_LABEL[d.type]} · {d.modelName}
                      {d.portCount > 0 && ` · ${d.portsConnected}/${d.portCount} puertos en uso`}
                    </span>
                  </div>
                  <StatusBadge status={DEVICE_STATUS_TONE[d.status]} label={DEVICE_STATUS_LABEL[d.status]} />
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
      {editingRack && <RackForm rack={rack} onClose={() => setEditingRack(false)} />}
      {adding !== null && <DeviceForm rackId={rack.id} positionU={adding} onClose={() => setAdding(null)} />}
    </>
  );
}

function RackBlock({
  device,
  row,
  draggable,
  onOpen,
}: {
  device: RackDevice;
  row: number;
  draggable: boolean;
  onOpen: () => void;
}) {
  const problems = device.portsWithProblems > 0;
  return (
    <button
      type="button"
      role="listitem"
      className="rack-device"
      data-type={device.type}
      data-status={problems ? 'advertencia' : DEVICE_STATUS_TONE[device.status]}
      style={{ gridRow: `${row} / span ${Math.max(device.heightUnits, 1)}` }}
      draggable={draggable}
      onDragStart={(e) => e.dataTransfer.setData('text/plain', String(device.id))}
      onClick={onOpen}
      title={`${device.name} · U${device.positionU}${device.heightUnits > 1 ? `–${device.positionU + device.heightUnits - 1}` : ''}`}
    >
      <span className="rack-u mono">{device.positionU + device.heightUnits - 1}</span>
      <span className="rack-device-name mono">{device.name}</span>
      <span className="rack-device-meta">
        {DEVICE_TYPE_LABEL[device.type]}
        {device.portCount > 0 && ` · ${device.portsConnected}/${device.portCount}`}
        {problems && ` · ${device.portsWithProblems} con problemas`}
      </span>
    </button>
  );
}

import { useState, type DragEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { RackDevice, RackItem, RoomItem } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { StatusBadge } from '@components/StatusBadge';
import { usePanelApi } from '../../data/PanelData';
import { DeviceForm, RackForm, RoomForm } from './NetworkForms';
import { DEVICE_STATUS_LABEL, DEVICE_STATUS_TONE, DEVICE_TYPE_LABEL } from './netLabels';

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

  if (rooms.isPending) return <Loading />;
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
                    {r.branchName} · {r.rackCount} gabinetes · {r.deviceCount} equipos
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
  onEditRoom,
  onAddRack,
}: {
  room: RoomItem;
  canEdit: boolean;
  onOpenRack: (id: number) => void;
  onEditRoom: () => void;
  onAddRack: () => void;
}) {
  const api = usePanelApi();
  const racks = useQuery({ queryKey: ['red', 'gabinetes', room.id], queryFn: () => api.red.racks(room.id) });

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
      <p className="muted">
        {room.branchName}
        {room.location && ` · ${room.location}`}
      </p>
      {racks.isPending ? (
        <Loading />
      ) : racks.isError ? (
        <ErrorState error={racks.error} />
      ) : racks.data.length === 0 ? (
        <EmptyState title="La sala no tiene gabinetes" />
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
                {rack.usedUnits}/{rack.heightUnits} U · {rack.deviceCount} equipos
                {!rack.isActive && ' · inactivo'}
              </span>
            </button>
          ))}
        </div>
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

  const move = useMutation({
    mutationFn: ({ id, u }: { id: number; u: number }) => api.red.placeDevice(id, rackId, u),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['red'] }),
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
            canEdit && (
              <button type="button" className="btn btn-outline" onClick={() => setEditingRack(true)}>
                Editar gabinete
              </button>
            )
          }
        >
          {move.isError && (
            <p className="auth-error" role="alert">
              {move.error.message}
            </p>
          )}
          {canEdit && (
            <p className="muted note">
              Arrastre un equipo a otra U para moverlo, o haga clic en una U libre para agregar uno.
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
        </Card>

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

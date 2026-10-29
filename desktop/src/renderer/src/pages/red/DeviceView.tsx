import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DeviceDetail, PortItem } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { StatusBadge } from '@components/StatusBadge';
import { dateTime, timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { CableDialog, DeviceForm, PathDialog, PortForm } from './NetworkForms';
import {
  DEVICE_STATUS_LABEL,
  DEVICE_STATUS_TONE,
  DEVICE_TYPE_LABEL,
  PORT_STATUS_LABEL,
  PORT_TYPE_LABEL,
  portTone,
} from './netLabels';

interface DeviceViewProps {
  deviceId: number;
  canEdit: boolean;
  onBack: () => void;
  onOpenEquipo: (id: number) => void;
}

/** W-46 Detalle de switch (o de cualquier dispositivo) y W-47 panel de puerto. */
export function DeviceView({ deviceId, canEdit, onBack, onOpenEquipo }: DeviceViewProps) {
  const api = usePanelApi();
  const query = useQuery({ queryKey: ['red', 'dispositivo', deviceId], queryFn: () => api.red.device(deviceId) });
  const [selected, setSelected] = useState<number | null>(null);
  const [editing, setEditing] = useState(false);
  const [retiring, setRetiring] = useState(false);

  return (
    <>
      <nav className="breadcrumb" aria-label="Ruta">
        <button type="button" className="link-quiet" onClick={onBack}>
          Red
        </button>
        {query.data?.rackName && (
          <>
            <span aria-hidden="true">›</span>
            <span>
              {query.data.roomName} · {query.data.rackName}
            </span>
          </>
        )}
        <span aria-hidden="true">›</span>
        <strong className="mono">{query.data?.name ?? `#${deviceId}`}</strong>
      </nav>
      {query.isPending ? (
        <Loading />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <>
          <DeviceHeader
            device={query.data}
            canEdit={canEdit}
            onEdit={() => setEditing(true)}
            onRetire={() => setRetiring(true)}
          />
          <div className="ficha-grid">
            <div className="ficha-main">
              <PortGrid device={query.data} selected={selected} onSelect={setSelected} />
            </div>
            <div className="ficha-side">
              {selected !== null && query.data.ports.some((p) => p.id === selected) ? (
                <PortPanel
                  device={query.data}
                  port={query.data.ports.find((p) => p.id === selected)!}
                  canEdit={canEdit}
                  onOpenEquipo={onOpenEquipo}
                />
              ) : (
                <DeviceInfo device={query.data} />
              )}
            </div>
          </div>
          {editing && <DeviceForm device={query.data} onClose={() => setEditing(false)} />}
          {retiring && <RetireDeviceDialog device={query.data} onClose={() => setRetiring(false)} onRetired={onBack} />}
        </>
      )}
    </>
  );
}

function DeviceHeader({
  device,
  canEdit,
  onEdit,
  onRetire,
}: {
  device: DeviceDetail;
  canEdit: boolean;
  onEdit: () => void;
  onRetire: () => void;
}) {
  const connected = device.ports.filter((p) => p.status === 'conectado').length;
  const problems = device.ports.filter((p) => p.status === 'errores' || p.status === 'caido').length;
  return (
    <div className="ficha-head">
      <span className="status-orb" data-status={DEVICE_STATUS_TONE[device.status]} aria-hidden="true">
        <span />
      </span>
      <div className="ficha-id">
        <div className="ficha-title">
          <h1 className="mono">{device.name}</h1>
          <StatusBadge status={DEVICE_STATUS_TONE[device.status]} label={DEVICE_STATUS_LABEL[device.status]} />
        </div>
        <ul className="ficha-meta">
          <li>
            {DEVICE_TYPE_LABEL[device.type]} · {device.brand} {device.modelName}
          </li>
          <li>
            <Icon name="pin" size={14} />
            {device.branchName}
            {device.rackName ? ` · ${device.rackName} U${device.positionU}` : ' · fuera de gabinete'}
          </li>
          {device.managementIp && <li className="mono">{device.managementIp}</li>}
          {device.ports.length > 0 && (
            <li>
              {connected}/{device.ports.length} puertos en uso{problems > 0 && ` · ${problems} con problemas`}
            </li>
          )}
          {device.isActiveDevice && (
            <li>
              <Icon name="clock" size={14} />
              {device.lastPolledAt ? `Último sondeo ${timeAgo(device.lastPolledAt)}` : 'Sin sondeo SNMP todavía'}
            </li>
          )}
        </ul>
      </div>
      {canEdit && device.status !== 'baja' && (
        <div className="ficha-actions">
          <button type="button" className="btn btn-outline" onClick={onEdit}>
            Editar
          </button>
          <button type="button" className="btn btn-outline" onClick={onRetire}>
            Dar de baja
          </button>
        </div>
      )}
    </div>
  );
}

function PortGrid({
  device,
  selected,
  onSelect,
}: {
  device: DeviceDetail;
  selected: number | null;
  onSelect: (id: number) => void;
}) {
  if (device.ports.length === 0)
    return (
      <Card title="Puertos">
        <p className="muted">Este equipo no tiene puertos de red.</p>
      </Card>
    );
  return (
    <Card
      title="Puertos"
      aside={
        <ul className="legend port-legend">
          <li data-port="conectado">Conectado</li>
          <li data-port="libre">Libre</li>
          <li data-port="errores">Errores</li>
          <li data-port="caido">Caído</li>
          <li data-port="troncal">Troncal</li>
        </ul>
      }
    >
      <div className="port-grid" role="list" aria-label={`Puertos de ${device.name}`}>
        {device.ports.map((port) => (
          <button
            key={port.id}
            type="button"
            role="listitem"
            className="port"
            data-port={portTone(port)}
            data-kind={port.type === 'rj45' ? 'rj45' : 'sfp'}
            aria-pressed={selected === port.id}
            aria-label={`Puerto ${port.name ?? port.number}: ${port.isTrunk ? 'troncal' : PORT_STATUS_LABEL[port.status]}`}
            title={[port.name ?? port.number, port.machineName ?? port.cableTo].filter(Boolean).join(' · ')}
            onClick={() => onSelect(port.id)}
          >
            <span className="mono">{port.number}</span>
            {port.cableId !== null && <span className="port-cable" aria-hidden="true" />}
          </button>
        ))}
      </div>
    </Card>
  );
}

function DeviceInfo({ device }: { device: DeviceDetail }) {
  const rows: [string, string | null][] = [
    ['Tipo', DEVICE_TYPE_LABEL[device.type]],
    ['Modelo', `${device.brand} ${device.modelName}`],
    ['Sucursal', device.branchName],
    [
      'Ubicación',
      device.rackName ? `${device.roomName} · ${device.rackName} · U${device.positionU}` : 'Fuera de gabinete',
    ],
    ['IP de gestión', device.managementIp],
    ['Firmware', device.firmware],
    [
      'SNMPv3',
      device.isActiveDevice
        ? device.snmpUser
          ? `${device.snmpUser}${device.hasSnmpCredential ? ' · claves guardadas' : ' · sin claves'}`
          : 'Sin configurar'
        : 'No aplica (pasivo)',
    ],
    ['Registrado', dateTime(device.createdAt)],
  ];
  return (
    <Card title="Información">
      <dl className="info-list">
        {rows.map(([label, value]) => (
          <div key={label}>
            <dt>{label}</dt>
            <dd className={label === 'IP de gestión' ? 'mono' : undefined}>{value ?? '—'}</dd>
          </div>
        ))}
      </dl>
      <p className="muted note">Elija un puerto para ver qué tiene conectado.</p>
    </Card>
  );
}

function PortPanel({
  device,
  port,
  canEdit,
  onOpenEquipo,
}: {
  device: DeviceDetail;
  port: PortItem;
  canEdit: boolean;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [dialog, setDialog] = useState<'editar' | 'cable' | 'ruta' | null>(null);
  const unplug = useMutation({
    mutationFn: (id: number) => api.red.deleteCable(id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['red'] }),
  });
  const passive = device.type === 'patch_panel';
  // Un puerto de patch panel admite dos cables (roseta y switch).
  const canConnect = canEdit && (port.cableId === null || (passive && !(port.cableTo ?? '').includes(' · ')));

  return (
    <Card
      title={`Puerto ${port.name ?? port.number}`}
      aside={
        <span className="port-chip" data-port={portTone(port)}>
          {port.isTrunk ? 'Troncal' : PORT_STATUS_LABEL[port.status]}
        </span>
      }
    >
      {unplug.isError && (
        <p className="auth-error" role="alert">
          {unplug.error.message}
        </p>
      )}
      <dl className="info-list">
        <div>
          <dt>Número</dt>
          <dd className="mono">{port.number}</dd>
        </div>
        <div>
          <dt>Tipo</dt>
          <dd>{PORT_TYPE_LABEL[port.type]}</dd>
        </div>
        <div>
          <dt>VLAN</dt>
          <dd className="mono">{port.vlan ?? '—'}</dd>
        </div>
        <div>
          <dt>Velocidad</dt>
          <dd className="mono">
            {port.speedMbps
              ? port.speedMbps >= 1000
                ? `${port.speedMbps / 1000} Gbps`
                : `${port.speedMbps} Mbps`
              : '—'}
          </dd>
        </div>
        <div>
          <dt>Cableado</dt>
          <dd>{port.cableTo ?? 'Sin cable documentado'}</dd>
        </div>
        <div>
          <dt>Equipo detectado</dt>
          <dd>
            {port.machineId !== null ? (
              <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(port.machineId!)}>
                {port.machineName}
              </button>
            ) : (
              '—'
            )}
          </dd>
        </div>
      </dl>
      {port.isVmHost && <p className="muted note">Servidor con máquinas virtuales.</p>}
      <div className="port-actions">
        <button type="button" className="btn btn-outline" onClick={() => setDialog('ruta')}>
          Ver ruta completa
        </button>
        {canEdit && (
          <button type="button" className="btn btn-outline" onClick={() => setDialog('editar')}>
            Editar puerto
          </button>
        )}
        {canConnect && (
          <button type="button" className="btn btn-primary" onClick={() => setDialog('cable')}>
            Conectar cable
          </button>
        )}
        {canEdit && port.cableId !== null && (
          <button
            type="button"
            className="btn btn-outline"
            disabled={unplug.isPending}
            onClick={() => unplug.mutate(port.cableId!)}
          >
            Quitar cable
          </button>
        )}
      </div>
      {dialog === 'editar' && <PortForm port={port} onClose={() => setDialog(null)} />}
      {dialog === 'cable' && <CableDialog device={device} port={port} onClose={() => setDialog(null)} />}
      {dialog === 'ruta' && (
        <PathDialog
          from={{ puerto: port.id }}
          title={`Ruta de ${device.name} ${port.name ?? port.number}`}
          onClose={() => setDialog(null)}
        />
      )}
    </Card>
  );
}

function RetireDeviceDialog({
  device,
  onClose,
  onRetired,
}: {
  device: DeviceDetail;
  onClose: () => void;
  onRetired: () => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const retire = useMutation({
    mutationFn: () => api.red.retireDevice(device.id),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['red'] });
      onRetired();
    },
  });
  return (
    <Modal
      title={`Dar de baja ${device.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="button" className="btn btn-primary" disabled={retire.isPending} onClick={() => retire.mutate()}>
            Dar de baja
          </button>
        </>
      }
    >
      {retire.isError && (
        <p className="auth-error" role="alert">
          {retire.error.message}
        </p>
      )}
      <p>
        El equipo deja de consultarse y libera su lugar en el gabinete. Se conserva su historial y su cableado
        documentado.
      </p>
    </Modal>
  );
}

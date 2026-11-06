import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DeviceDetail, PortItem } from '@monitoreoti/shared';
import { ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Modal } from '@components/Modal';
import { StatusBadge } from '@components/StatusBadge';
import { dateTime, timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { CableDialog, DeviceForm, PathDialog, PortAssignDialog, PortForm } from './NetworkForms';
import {
  DEVICE_STATUS_LABEL,
  DEVICE_STATUS_TONE,
  DEVICE_TYPE_LABEL,
  PORT_STATUS_LABEL,
  PORT_TYPE_LABEL,
  portTone,
} from './netLabels';
import { ApiTestButton, apiStatusText, offersApi } from './FortiGateApi';
import { ManagedSwitches } from './ManagedSwitches';
import { NetworkHistory } from './NetworkHistory';

interface DeviceViewProps {
  deviceId: number;
  canEdit: boolean;
  onBack: () => void;
  onOpenEquipo: (id: number) => void;
  /** Abrir otro dispositivo (un FortiSwitch desde su FortiGate). */
  onOpenDevice?: (id: number) => void;
}

/** W-46 Detalle de switch (o de cualquier dispositivo) y W-47 panel de puerto. */
export function DeviceView({ deviceId, canEdit, onBack, onOpenEquipo, onOpenDevice }: DeviceViewProps) {
  const api = usePanelApi();
  // "Sondear ahora": lo que había antes del pedido; mientras no cambie, se vuelve a mirar cada pocos segundos.
  const [polling, setPolling] = useState<{ polled: string | null; error: string | null } | null>(null);
  const query = useQuery({
    queryKey: ['red', 'dispositivo', deviceId],
    queryFn: () => api.red.device(deviceId),
    refetchInterval: (q) =>
      polling && q.state.data?.lastPolledAt === polling.polled && q.state.data?.lastError === polling.error
        ? 3_000
        : false,
  });
  const pollNow = useMutation({
    mutationFn: () => api.red.pollDevice(deviceId),
    onSuccess: () => {
      setPolling({ polled: query.data?.lastPolledAt ?? null, error: query.data?.lastError ?? null });
      // Un equipo que sigue sin responder deja el mismo error: a los 45 s se da por terminado.
      setTimeout(() => setPolling(null), 45_000);
    },
  });
  const waiting =
    pollNow.isPending ||
    (polling !== null && query.data?.lastPolledAt === polling.polled && query.data?.lastError === polling.error);
  const [selected, setSelected] = useState<number | null>(null);
  const [editing, setEditing] = useState(false);
  const [retiring, setRetiring] = useState(false);

  return (
    <>
      <nav className="breadcrumb" aria-label="Ruta">
        <button type="button" className="link-quiet" onClick={onBack}>
          Red
        </button>
        {query.data?.roomName && (
          <>
            <span aria-hidden="true">›</span>
            <span>{query.data.roomName}</span>
          </>
        )}
        {query.data?.rackName && (
          <>
            <span aria-hidden="true">›</span>
            <span>{query.data.rackName}</span>
          </>
        )}
        <span aria-hidden="true">›</span>
        <strong className="mono">{query.data?.name ?? `#${deviceId}`}</strong>
      </nav>
      {query.isPending ? (
        <Loading variant="detail" />
      ) : query.isError ? (
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      ) : (
        <>
          <DeviceHeader
            device={query.data}
            canEdit={canEdit}
            onEdit={() => setEditing(true)}
            onRetire={() => setRetiring(true)}
            onPollNow={() => pollNow.mutate()}
            polling={waiting}
          />
          {pollNow.isError && (
            <p className="auth-error" role="alert">
              {pollNow.error.message}
            </p>
          )}
          {query.data.lastError && <PollError device={query.data} />}
          <div className="ficha-grid device-grid">
            <div className="ficha-main">
              <PortGrid device={query.data} selected={selected} onSelect={setSelected} />
              <ManagedSwitches device={query.data} canEdit={canEdit} onOpenDevice={onOpenDevice} />
              <NetworkHistory entity="dispositivos_red" entityId={deviceId} cardTitle="Historial" />
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
                <DeviceInfo device={query.data} canEdit={canEdit} />
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
  onPollNow,
  polling,
}: {
  device: DeviceDetail;
  canEdit: boolean;
  onEdit: () => void;
  onRetire: () => void;
  onPollNow: () => void;
  polling: boolean;
}) {
  const connected = device.ports.filter((p) => p.status === 'conectado').length;
  const problems = device.ports.filter((p) => p.status === 'errores' || p.status === 'caido').length;
  return (
    <div className="ficha-head device-head">
      <div className="ficha-id">
        <div className="ficha-title">
          <h1 className="mono">{device.name}</h1>
          <StatusBadge status={DEVICE_STATUS_TONE[device.status]} label={DEVICE_STATUS_LABEL[device.status]} />
        </div>
        <ul className="ficha-meta device-meta">
          <li>
            {DEVICE_TYPE_LABEL[device.type]} · {device.brand} {device.modelName}
          </li>
          <li>
            {device.branchName}
            {device.rackName ? ` · ${device.rackName} · U${device.positionU}` : ' · fuera de gabinete'}
          </li>
          {device.managementIp && (
            <li>
              <strong className="mono">{device.managementIp}</strong>
            </li>
          )}
          {device.ports.length > 0 && (
            <li>
              <strong className="mono">
                {connected}/{device.ports.length}
              </strong>{' '}
              puertos en uso
              {problems > 0 && (
                <>
                  {' · '}
                  <strong className="mono is-late">{problems}</strong> con problemas
                </>
              )}
            </li>
          )}
          {device.isActiveDevice && (
            <li>
              {device.fortiGateName ? (
                <>
                  Datos del FortiGate <strong className="mono">{device.fortiGateName}</strong>
                </>
              ) : device.collectorName ? (
                <>
                  Lo consulta <strong className="mono">{device.collectorName}</strong>
                </>
              ) : (
                'Sin recolector asignado'
              )}
              {device.lastPolledAt && !device.lastError && <> · sondeo {timeAgo(device.lastPolledAt)}</>}
            </li>
          )}
        </ul>
      </div>
      {canEdit && device.status !== 'baja' && (
        <div className="ficha-actions">
          {device.isActiveDevice && device.collectorId !== null && (
            <button
              type="button"
              className="btn btn-outline"
              disabled={polling}
              title="El recolector lo consulta ahora, sin esperar su turno"
              onClick={onPollNow}
            >
              {polling ? 'Sondeando…' : 'Sondear ahora'}
            </button>
          )}
          <button type="button" className="btn btn-outline" onClick={onEdit}>
            Editar
          </button>
          <button type="button" className="btn btn-outline btn-danger" onClick={onRetire}>
            Dar de baja
          </button>
        </div>
      )}
    </div>
  );
}

/** Código corto del fallo, para buscarlo o reportarlo (el texto completo va al lado). */
export function pollErrorCode(message: string): string {
  const text = message.toLowerCase();
  if (text.includes('clave')) return 'ERR_SNMP_CREDENCIAL';
  if (text.includes('autentic') || text.includes('usuario')) return 'ERR_SNMP_AUTH';
  if (text.includes('no respondió') || text.includes('tardó') || text.includes('timeout')) return 'ERR_SNMP_TIMEOUT';
  return 'ERR_SNMP';
}

/** Franja del último sondeo fallido: qué pasó, su código y desde cuándo no responde. */
function PollError({ device }: { device: DeviceDetail }) {
  const message = device.lastError!;
  return (
    <div className="poll-error" role="alert">
      <span>Último sondeo fallido: {message.charAt(0).toLowerCase() + message.slice(1)}</span>
      <code className="mono">{pollErrorCode(message)}</code>
      <span className="poll-error-since mono">
        {device.lastPolledAt ? `sin respuesta desde ${timeAgo(device.lastPolledAt)}` : 'nunca respondió'}
      </span>
    </div>
  );
}

/** Cuenta de puertos por tipo para la placa ("24 × RJ45 · 4 × SFP+"). */
function portSummary(ports: PortItem[]): string {
  const counts = new Map<string, number>();
  for (const port of ports) {
    const label = PORT_TYPE_LABEL[port.type].toUpperCase();
    counts.set(label, (counts.get(label) ?? 0) + 1);
  }
  return [...counts].map(([label, n]) => `${n} × ${label}`).join(' · ');
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
  // La placa como el equipo real: cobre en dos filas (impares arriba) y los uplinks aparte.
  const byNumber = [...device.ports].sort((a, b) => a.number - b.number);
  const copper = byNumber.filter((p) => p.type === 'rj45');
  const uplinks = byNumber.filter((p) => p.type !== 'rj45');
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
      <div className="faceplate">
        <div className="faceplate-head mono" aria-hidden="true">
          <span>{`${device.brand} ${device.modelName}`.trim().toUpperCase()}</span>
          <span>{portSummary(device.ports)}</span>
        </div>
        <div className="faceplate-ports" role="list" aria-label={`Puertos de ${device.name}`}>
          {[copper, uplinks].map(
            (group, i) =>
              group.length > 0 && (
                <div key={i} className="port-grid">
                  {group.map((port) => (
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
              ),
          )}
        </div>
      </div>
      <p className="muted note">
        Elija un puerto para ver qué tiene conectado.
        {device.isActiveDevice && !device.lastPolledAt && ' Los estados aparecen cuando el switch responda por SNMP.'}
      </p>
    </Card>
  );
}

/** Datos técnicos que se muestran en mono (se copian tal cual). */
const MONO_FIELDS = new Set(['IP de gestión', 'Firmware', 'Registrado']);

function DeviceInfo({ device, canEdit }: { device: DeviceDetail; canEdit: boolean }) {
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
    ...(device.fortiGateId
      ? ([
          ['Datos', `Por la API de ${device.fortiGateName ?? 'su FortiGate'}`],
          ['Serie', device.serialNumber ?? null],
        ] as [string, string | null][])
      : []),
    [
      'SNMPv3',
      device.fortiGateId
        ? 'No hace falta (datos del FortiGate)'
        : device.isActiveDevice
          ? device.snmpUser
            ? `${device.snmpUser}${device.hasSnmpCredential ? ' · claves guardadas' : ' · sin claves'}`
            : 'Sin configurar'
          : 'No aplica (pasivo)',
    ],
    ...(device.isActiveDevice && offersApi(device.type, device.apiUrl)
      ? ([['API REST', apiStatusText(device)]] as [string, string | null][])
      : []),
    ['Registrado', dateTime(device.createdAt)],
  ];
  return (
    <Card title="Información">
      <dl className="info-list">
        {rows.map(([label, value]) => (
          <div key={label}>
            <dt>{label}</dt>
            <dd className={MONO_FIELDS.has(label) ? 'mono strong' : undefined}>{value ?? '—'}</dd>
          </div>
        ))}
      </dl>
      {device.apiLastError && (
        <p className="api-test-result is-error" role="alert">
          {device.apiLastError}
        </p>
      )}
      {canEdit && device.hasApiToken && device.status !== 'baja' && (
        <ApiTestButton
          deviceId={device.id}
          input={{ apiUrl: null, apiToken: null, apiAceptarCertificado: device.apiAcceptSelfSigned ?? true }}
        />
      )}
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
  const [dialog, setDialog] = useState<'editar' | 'cable' | 'ruta' | 'asignar' | null>(null);
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
          <dt>{port.isManualAssignment ? 'Equipo (asignado a mano)' : 'Equipo detectado'}</dt>
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
        {canEdit && !passive && (
          <button type="button" className="btn btn-outline" onClick={() => setDialog('asignar')}>
            Asignar a mano
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
      {dialog === 'asignar' && <PortAssignDialog device={device} port={port} onClose={() => setDialog(null)} />}
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

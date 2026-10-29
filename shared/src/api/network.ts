/**
 * Cliente del data center (/api/v1/red, Fase 4). Tipos espejo de Application/Network/NetworkContracts.cs.
 */
import type { PortStatus } from '../status';

export type DeviceType =
  | 'switch'
  | 'router'
  | 'firewall'
  | 'servidor'
  | 'patch_panel'
  | 'ups'
  | 'pdu'
  | 'bandeja'
  | 'organizador'
  | 'access_point'
  | 'otro';

export type DeviceStatus = 'en_linea' | 'advertencia' | 'critico' | 'sin_respuesta' | 'pasivo' | 'baja';
export type PortType = 'rj45' | 'sfp' | 'sfp_plus' | 'fibra' | 'consola' | 'otro';
export type CableType = 'cat5e' | 'cat6' | 'cat6a' | 'cat7' | 'fibra_mm' | 'fibra_sm' | 'dac' | 'otro';

export interface RoomItem {
  id: number;
  name: string;
  location: string | null;
  branchId: number;
  branchName: string;
  rackCount: number;
  deviceCount: number;
}

export interface RackItem {
  id: number;
  name: string;
  roomId: number;
  roomName: string;
  branchId: number;
  branchName: string;
  heightUnits: number;
  usedUnits: number;
  row: string | null;
  position: number | null;
  isActive: boolean;
  deviceCount: number;
  qrCode: string;
}

export interface RackDevice {
  id: number;
  name: string;
  type: DeviceType;
  modelName: string;
  positionU: number;
  heightUnits: number;
  status: DeviceStatus;
  portCount: number;
  portsConnected: number;
  portsWithProblems: number;
}

export interface RackDetail {
  rack: RackItem;
  devices: RackDevice[];
}

export interface DeviceModelItem {
  id: number;
  brand: string;
  model: string;
  type: DeviceType;
  heightUnits: number;
  portCount: number;
  portDescription: string | null;
  isActiveDevice: boolean;
  /** Catálogo base compartido (solo lectura). */
  isShared: boolean;
}

export interface DeviceListItem {
  id: number;
  name: string;
  type: DeviceType;
  brand: string;
  modelName: string;
  branchId: number;
  branchName: string;
  rackId: number | null;
  rackName: string | null;
  roomName: string | null;
  positionU: number | null;
  heightUnits: number;
  managementIp: string | null;
  status: DeviceStatus;
  lastPolledAt: string | null;
  portCount: number;
  portsConnected: number;
  portsWithProblems: number;
}

export interface PortItem {
  id: number;
  number: number;
  name: string | null;
  type: PortType;
  vlan: number | null;
  speedMbps: number | null;
  status: PortStatus;
  isTrunk: boolean;
  isVmHost: boolean;
  isManualAssignment: boolean;
  cableId: number | null;
  cableTo: string | null;
  machineId: number | null;
  machineName: string | null;
}

export interface DeviceDetail {
  id: number;
  name: string;
  type: DeviceType;
  modelId: number;
  brand: string;
  modelName: string;
  isActiveDevice: boolean;
  branchId: number;
  branchName: string;
  rackId: number | null;
  rackName: string | null;
  roomId: number | null;
  roomName: string | null;
  positionU: number | null;
  heightUnits: number;
  managementIp: string | null;
  firmware: string | null;
  status: DeviceStatus;
  lastPolledAt: string | null;
  snmpUser: string | null;
  hasSnmpCredential: boolean;
  qrCode: string;
  createdAt: string;
  ports: PortItem[];
}

export interface OutletItem {
  id: number;
  code: string;
  location: string | null;
  branchId: number;
  branchName: string;
  cableId: number | null;
  portId: number | null;
  portLabel: string | null;
  qrCode: string;
}

export interface CableItem {
  id: number;
  portAId: number;
  portALabel: string;
  portBId: number | null;
  portBLabel: string | null;
  outletId: number | null;
  outletCode: string | null;
  type: CableType | null;
  color: string | null;
  label: string | null;
  notes: string | null;
  createdAt: string;
}

export interface PathHop {
  /** roseta, patch_panel, switch, router, uplink… */
  kind: string;
  deviceId: number | null;
  name: string;
  detail: string;
  cableLabel: string | null;
}

export interface RoomInput {
  nombre: string;
  ubicacion?: string | null;
  sucursalId: number;
}

export interface RackInput {
  nombre: string;
  salaId: number;
  alturaU: number;
  fila?: string | null;
  posicion?: number | null;
  activo?: boolean;
}

export interface DeviceModelInput {
  marca: string;
  modelo: string;
  tipo: DeviceType;
  alturaU: number;
  cantidadPuertos: number;
  tipoPuertos?: string | null;
  esActivo: boolean;
}

export interface DeviceInput {
  nombre: string;
  modeloId: number;
  sucursalId: number;
  gabineteId?: number | null;
  posicionU?: number | null;
  ipGestion?: string | null;
  firmware?: string | null;
  snmpUsuario?: string | null;
  /** Solo para cambiarlas; null conserva las guardadas. */
  snmpClaveAutenticacion?: string | null;
  snmpClavePrivacidad?: string | null;
}

export interface PortInput {
  nombre: string | null;
  tipo: PortType;
  vlan: number | null;
  esHostVm: boolean;
}

export interface OutletInput {
  codigo: string;
  ubicacion?: string | null;
  sucursalId: number;
}

export interface CableInput {
  puertoAId: number;
  puertoBId?: number | null;
  puntoRedId?: number | null;
  tipoCable?: CableType | null;
  color?: string | null;
  etiqueta?: string | null;
  notas?: string | null;
}

export interface DeviceFilter {
  q?: string;
  tipo?: string;
  sucursal?: number | null;
  sala?: number | null;
  gabinete?: number | null;
}

type Id = { id: number };

export interface NetworkApi {
  rooms(): Promise<RoomItem[]>;
  racks(roomId?: number | null): Promise<RackItem[]>;
  rack(id: number): Promise<RackDetail>;
  models(): Promise<DeviceModelItem[]>;
  devices(filter?: DeviceFilter): Promise<DeviceListItem[]>;
  device(id: number): Promise<DeviceDetail>;
  outlets(branchId?: number | null, q?: string): Promise<OutletItem[]>;
  cables(deviceId?: number | null): Promise<CableItem[]>;
  path(from: { roseta?: number; puerto?: number }): Promise<PathHop[]>;
  saveRoom(id: number | null, input: RoomInput): Promise<Id>;
  deleteRoom(id: number): Promise<Id>;
  saveRack(id: number | null, input: RackInput): Promise<Id>;
  deleteRack(id: number): Promise<Id>;
  saveModel(id: number | null, input: DeviceModelInput): Promise<Id>;
  saveDevice(id: number | null, input: DeviceInput): Promise<Id>;
  placeDevice(id: number, rackId: number | null, positionU: number | null): Promise<Id>;
  retireDevice(id: number): Promise<Id>;
  savePort(id: number, input: PortInput): Promise<Id>;
  saveOutlet(id: number | null, input: OutletInput): Promise<Id>;
  deleteOutlet(id: number): Promise<Id>;
  createCable(input: CableInput): Promise<Id>;
  deleteCable(id: number): Promise<Id>;
}

export function createNetworkApi(
  get: <T>(path: string, query?: Record<string, string | number | null | undefined>) => Promise<T>,
  send: <T>(method: string, path: string, body?: unknown) => Promise<T>,
): NetworkApi {
  const save = <T>(path: string, id: number | null, input: T) =>
    id === null ? send<Id>('POST', path, input) : send<Id>('PUT', `${path}/${id}`, input);

  return {
    rooms: () => get('/red/salas'),
    racks: (roomId) => get('/red/gabinetes', { sala: roomId }),
    rack: (id) => get(`/red/gabinetes/${id}`),
    models: () => get('/red/modelos'),
    devices: (filter = {}) => get('/red/dispositivos', { ...filter }),
    device: (id) => get(`/red/dispositivos/${id}`),
    outlets: (branchId, q) => get('/red/rosetas', { sucursal: branchId, q }),
    cables: (deviceId) => get('/red/cables', { dispositivo: deviceId }),
    path: (from) => get('/red/ruta', from),
    saveRoom: (id, input) => save('/red/salas', id, input),
    deleteRoom: (id) => send('DELETE', `/red/salas/${id}`),
    saveRack: (id, input) => save('/red/gabinetes', id, input),
    deleteRack: (id) => send('DELETE', `/red/gabinetes/${id}`),
    saveModel: (id, input) => save('/red/modelos', id, input),
    saveDevice: (id, input) => save('/red/dispositivos', id, input),
    placeDevice: (id, rackId, positionU) =>
      send('PUT', `/red/dispositivos/${id}/ubicacion`, { gabineteId: rackId, posicionU: positionU }),
    retireDevice: (id) => send('POST', `/red/dispositivos/${id}/baja`),
    savePort: (id, input) => send('PUT', `/red/puertos/${id}`, input),
    saveOutlet: (id, input) => save('/red/rosetas', id, input),
    deleteOutlet: (id) => send('DELETE', `/red/rosetas/${id}`),
    createCable: (input) => send('POST', '/red/cables', input),
    deleteCable: (id) => send('DELETE', `/red/cables/${id}`),
  };
}

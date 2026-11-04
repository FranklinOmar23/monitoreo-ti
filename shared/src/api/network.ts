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
  /** Agente que lo consulta por SNMP; null = nadie lo consulta. */
  collectorId: number | null;
  collectorName: string | null;
  /** Por qué falló el último sondeo SNMP (null si respondió). */
  lastError: string | null;
}

/** Un equipo con agente que puede consultar por SNMP los equipos de su sucursal. */
export interface CollectorItem {
  agentId: number;
  machineId: number;
  machineName: string;
  branchId: number;
  ipAddress: string | null;
  online: boolean;
  lastContactAt: string | null;
  deviceCount: number;
}

/** W-42 Resumen de red. */
export interface NetworkSummary {
  devices: number;
  online: number;
  warning: number;
  critical: number;
  notResponding: number;
  withoutCollector: number;
  ports: number;
  portsConnected: number;
  portsWithProblems: number;
  trunks: number;
  unknownDevices: number;
  differences: number;
  openAlerts: number;
  collectors: number;
  collectorsOnline: number;
  attention: DeviceListItem[];
}

/** W-53 Una MAC sin equipo registrado; las aleatorias de un puerto se agrupan (count). */
export interface UnknownDeviceItem {
  mac: string;
  vendor: string | null;
  ip: string | null;
  vlan: number | null;
  deviceId: number;
  deviceName: string;
  portId: number;
  portLabel: string;
  isRandom: boolean;
  count: number;
  firstSeenAt: string;
  lastSeenAt: string;
  viaAccessPoint: boolean;
}

/** W-52 Dónde se vio una MAC o una IP. */
export interface NetworkSearchResult {
  mac: string;
  ip: string | null;
  vendor: string | null;
  machineId: number | null;
  machineName: string | null;
  deviceId: number | null;
  deviceName: string | null;
  portId: number | null;
  portLabel: string | null;
  vlan: number | null;
  lastSeenAt: string | null;
  viaAccessPoint: boolean;
}

export type DifferenceKind = 'enlace_sin_cable' | 'cable_distinto' | 'equipo_sin_cable';

/** W-66 Lo detectado que no coincide con lo documentado. */
export interface DifferenceItem {
  kind: DifferenceKind;
  title: string;
  detail: string;
  deviceId: number | null;
  portId: number | null;
  machineId: number | null;
}

/** W-51 Mapa de topología. */
export interface TopologyNode {
  id: number;
  name: string;
  type: DeviceType;
  status: DeviceStatus;
  branchName: string;
  trunks: number;
}

export interface TopologyLink {
  sourceId: number;
  targetId: number;
  sourcePort: string;
  targetPort: string;
  /** lldp: lo ve SNMP; cable: solo está documentado. */
  kind: 'lldp' | 'cable';
}

export interface Topology {
  nodes: TopologyNode[];
  links: TopologyLink[];
}

/** "Conexión de red" de la ficha del equipo. */
export interface MachineNetworkInfo {
  deviceId: number;
  switchName: string;
  portId: number;
  portLabel: string;
  vlan: number | null;
  rackName: string | null;
  roomName: string | null;
  ipAddress: string | null;
  macAddress: string;
  lastSeenAt: string;
  viaAccessPoint: boolean;
  isManual: boolean;
}

/** W-67 Asignación manual; los dos null devuelven el puerto a lo que detecte SNMP. */
export interface PortAssignmentInput {
  maquinaId: number | null;
  troncal: boolean | null;
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
  /** Agente de la misma sucursal que lo consulta por SNMP. */
  recolectorId?: number | null;
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

export interface HistoryChange {
  field: string;
  before: string | null;
  after: string | null;
}

/** Un cambio del data center (W-69), tomado de la auditoría. */
export interface HistoryItem {
  id: number;
  at: string;
  action: string;
  title: string;
  entity: string;
  entityId: string | null;
  subject: string | null;
  /** null = lo hizo el sistema (SNMP, correlación). */
  user: string | null;
  changes: HistoryChange[];
}

export interface HistoryPage {
  items: HistoryItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface HistoryFilter {
  entidad?: string | null;
  id?: string | null;
  dias?: number;
  pagina?: number;
}

export type ImportType = 'dispositivos' | 'rosetas' | 'cables';

export interface ImportRow {
  line: number;
  summary: string;
  errors: string[];
}

/** Vista previa o resultado de una importación: se guarda todo o nada. */
export interface ImportResult {
  type: ImportType;
  total: number;
  valid: number;
  applied: boolean;
  fileErrors: string[];
  rows: ImportRow[];
}

/** Columnas de cada importación (espejo de ImportColumns); con * las obligatorias. */
export const IMPORT_COLUMNS: Record<ImportType, string[]> = {
  dispositivos: [
    'nombre*',
    'marca*',
    'modelo*',
    'sucursal*',
    'sala',
    'gabinete',
    'posicion_u',
    'ip_gestion',
    'firmware',
  ],
  rosetas: ['codigo*', 'ubicacion', 'sucursal*'],
  cables: [
    'dispositivo_a*',
    'puerto_a*',
    'dispositivo_b',
    'puerto_b',
    'roseta',
    'tipo_cable',
    'color',
    'etiqueta',
    'notas',
  ],
};

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
  history(filter?: HistoryFilter): Promise<HistoryPage>;
  summary(): Promise<NetworkSummary>;
  collectors(branchId?: number | null): Promise<CollectorItem[]>;
  unknown(): Promise<UnknownDeviceItem[]>;
  differences(): Promise<DifferenceItem[]>;
  topology(): Promise<Topology>;
  search(q: string): Promise<NetworkSearchResult[]>;
  assignPort(id: number, input: PortAssignmentInput): Promise<Id>;
  previewImport(type: ImportType, content: string): Promise<ImportResult>;
  runImport(type: ImportType, content: string): Promise<ImportResult>;
}

export function createNetworkApi(
  get: <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) => Promise<T>,
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
    history: (filter = {}) => get('/red/historial', { ...filter }),
    summary: () => get('/red/resumen'),
    collectors: (branchId) => get('/red/recolectores', { sucursal: branchId }),
    unknown: () => get('/red/desconocidos'),
    differences: () => get('/red/diferencias'),
    topology: () => get('/red/topologia'),
    search: (q) => get('/red/buscar', { q }),
    assignPort: (id, input) => send('PUT', `/red/puertos/${id}/asignacion`, input),
    previewImport: (tipo, contenido) => send('POST', '/red/importar/vista-previa', { tipo, contenido }),
    runImport: (tipo, contenido) => send('POST', '/red/importar', { tipo, contenido }),
  };
}

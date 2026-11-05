/**
 * Cliente del API de lectura del panel y la app (/api/v1). Los tipos reflejan los DTO del servidor
 * (Application/Panel/PanelContracts.cs); fechas en ISO 8601 UTC.
 */
import type { MachineStatus } from '../status';
import { createHttp, type FetchLike } from './http';
import { createNetworkApi, type MachineNetworkInfo, type NetworkApi } from './network';
import { createRemoteApi, type RemoteApi } from './remote';
import { createReportsApi, type ReportsApi } from './reports';
import { createSettingsApi, type SettingsApi } from './settings';
import { createUsersApi, type UsersApi } from './users';

export interface StatusCounts {
  total: number;
  healthy: number;
  warning: number;
  critical: number;
  disconnected: number;
  online: number;
}

export interface DashboardData {
  kpis: {
    counts: StatusCounts;
    registeredThisWeek: number;
    criticalWithOpenTicket: number;
    disconnectedOver24h: number;
  };
  fleetHealth: { date: string; healthy: number; warning: number; critical: number; disconnected: number }[];
  branches: { id: number; name: string; province: string | null; counts: StatusCounts }[];
  recentAlerts: {
    id: number;
    message: string;
    level: 'advertencia' | 'critico';
    machineId: number | null;
    machineName: string | null;
    branchName: string | null;
    createdAt: string;
  }[];
  topProblems: { id: number; name: string; status: MachineStatus; summary: string; events: number }[];
}

export interface LatestMetrics {
  at: string;
  cpu: number | null;
  ram: number | null;
  disk: number | null;
  temperature: number | null;
}

export type MachineType = 'computadora' | 'laptop' | 'servidor' | 'impresora' | 'otro';

export interface MachineListItem {
  id: number;
  name: string;
  detail: string | null;
  branchId: number;
  branchName: string;
  type: MachineType;
  ipAddress: string | null;
  status: MachineStatus;
  metrics: LatestMetrics | null;
  lastReportAt: string | null;
}

export interface MachineList {
  page: { items: MachineListItem[]; total: number; page: number; pageSize: number };
  counts: StatusCounts;
  operatingSystems: string[];
}

export interface MachineFilter {
  q?: string;
  sucursal?: number | null;
  tipo?: string;
  estado?: string;
  so?: string;
  pagina?: number;
  tamano?: number;
}

export interface MachineDetail {
  id: number;
  qrCode: string;
  name: string;
  type: MachineType;
  status: MachineStatus;
  brand: string | null;
  model: string | null;
  serialNumber: string | null;
  operatingSystem: string | null;
  ipAddress: string | null;
  macAddress: string | null;
  assignedTo: string | null;
  purchaseDate: string | null;
  warrantyUntil: string | null;
  branchId: number;
  branchName: string;
  departmentId: number | null;
  departmentName: string | null;
  lastReportAt: string | null;
  createdAt: string;
  agentVersion: string | null;
  hasAgent: boolean;
  metrics: LatestMetrics | null;
  components: {
    id: number;
    type: string;
    description: string | null;
    capacity: string | null;
    health: 'sano' | 'advertencia' | 'critico' | 'desconocido';
    /** Salud SMART del disco, estado del antivirus… (ej. "148 sectores dañados · 41 °C"). */
    detail: string | null;
    updatedAt: string;
  }[];
  problems: OpenProblem[];
  openErrorCount: number;
  ramTotalMb: number | null;
  /** Procesos en ejecución en la última foto del agente. */
  processCount: number | null;
  volumes: VolumeItem[];
  /** Impresoras consultadas por SNMP. */
  snmp?: PrinterSnmpInfo | null;
}

/** Problema abierto con su explicación en lenguaje simple (motor de alertas). */
export interface OpenProblem {
  id: number;
  level: 'advertencia' | 'critico';
  message: string;
  detail: string | null;
  advice: string | null;
  /** De dónde sale (ej. "SMART · 148 sectores dañados"). */
  source: string | null;
  createdAt: string;
  /** Qué problema es ("servicio:Spooler"): define la acción que se ofrece. */
  code?: string | null;
}

export interface VolumeItem {
  drive: string;
  label: string | null;
  totalGb: number;
  freeGb: number;
  updatedAt: string;
}

export interface ServiceItem {
  name: string;
  displayName: string | null;
  state: 'en_ejecucion' | 'detenido' | 'otro';
  startMode: 'automatico' | 'automatico_retrasado' | 'manual' | 'deshabilitado';
  /** Se marca cuando un servicio automático que estaba en ejecución se detiene. */
  stoppedSince: string | null;
}

export type MetricRange = '1h' | '24h' | '7d';

export interface MetricPoint {
  at: string;
  cpu: number | null;
  ram: number | null;
  disk: number | null;
  temperature: number | null;
}

export interface SoftwareItem {
  name: string;
  version: string | null;
  publisher: string | null;
  installedOn: string | null;
}

export interface NetworkInterfaceItem {
  name: string;
  macAddress: string;
  ipAddress: string | null;
  type: 'cableada' | 'wifi' | 'virtual' | 'otra';
  speedMbps: number | null;
  updatedAt: string;
}

export interface ErrorEventItem {
  id: number;
  at: string;
  level: 'info' | 'advertencia' | 'error' | 'critico';
  source: string | null;
  code: number | null;
  message: string | null;
  plainMessage: string | null;
}

export interface ProcessItem {
  name: string;
  pid: number | null;
  cpu: number | null;
  ramMb: number | null;
  at: string;
}

export interface AuditItem {
  id: number;
  at: string;
  action: string;
  userName: string | null;
  details: string | null;
}

export interface BranchItem {
  id: number;
  name: string;
  province: string | null;
  address: string | null;
  managerId: number | null;
  managerName: string | null;
  isActive: boolean;
  counts: StatusCounts;
}

/** Etiqueta QR del equipo: el contenido abre la ficha al escanearlo con la app. */
export interface MachineQr {
  codigo: string;
  contenido: string;
  /** PNG como data URI. */
  png: string;
}

export interface MachineInput {
  nombre: string;
  tipo: MachineType;
  sucursalId: number;
  departamentoId?: number | null;
  marca?: string | null;
  modelo?: string | null;
  serie?: string | null;
  sistemaOperativo?: string | null;
  ip?: string | null;
  mac?: string | null;
  responsable?: string | null;
  /** AAAA-MM-DD. */
  fechaCompra?: string | null;
  garantiaHasta?: string | null;
  /** Impresoras: v2c o v3 (null = sin SNMP). La comunidad o las claves solo se envían para cambiarlas. */
  snmpVersion?: 'v2c' | 'v3' | null;
  snmpUsuario?: string | null;
  snmpComunidad?: string | null;
  snmpClaveAutenticacion?: string | null;
  snmpClavePrivacidad?: string | null;
  recolectorId?: number | null;
}

/** SNMP de una impresora (nunca trae la comunidad ni las claves). */
export interface PrinterSnmpInfo {
  version: 'v2c' | 'v3';
  user: string | null;
  hasCredential: boolean;
  collectorId: number | null;
  collectorName: string | null;
  lastPollAt: string | null;
  lastError: string | null;
}

export type MaintenanceStatus = 'programado' | 'vencido' | 'realizado';

/** W-27 Un mantenimiento del calendario. Fechas AAAA-MM-DD. */
export interface MaintenanceItem {
  id: number;
  machineId: number;
  machineName: string;
  machineType: MachineType;
  branchId: number;
  branchName: string;
  type: 'preventivo' | 'correctivo';
  description: string | null;
  scheduledDate: string | null;
  completedDate: string | null;
  technicianId: number | null;
  technicianName: string | null;
  status: MaintenanceStatus;
}

export interface MaintenanceList {
  items: MaintenanceItem[];
  /** Programados que ya pasaron sin hacerse (en cualquier fecha). */
  overdue: number;
}

export interface MaintenanceFilter {
  desde: string;
  hasta: string;
  sucursal?: number | null;
  maquina?: number | null;
}

export interface MaintenanceInput {
  maquinaId: number;
  tipo: 'preventivo' | 'correctivo';
  descripcion?: string | null;
  fechaProgramada?: string | null;
  fechaRealizada?: string | null;
  tecnicoId?: number | null;
}

export interface UserPreferences {
  /** Recibir por correo las alertas críticas. */
  notificarCorreo: boolean;
}

export interface BranchInput {
  nombre: string;
  provincia?: string | null;
  direccion?: string | null;
  responsableId?: number | null;
  activa?: boolean;
}

/** A qué apunta un código QR. */
export interface QrTarget {
  type: 'maquina' | 'gabinete' | 'dispositivo' | 'roseta';
  id: number;
}

export interface QrLabel {
  id: number;
  nombre: string;
  sucursal: string;
  codigo: string;
  png: string;
}

export interface Option {
  id: number;
  nombre: string;
}

export interface InstallerToken {
  id: number;
  name: string | null;
  branchId: number;
  branchName: string;
  expiresAt: string;
  maxUses: number | null;
  uses: number;
  requiresApproval: boolean;
  revokedAt: string | null;
  createdAt: string;
  createdBy: string | null;
  state: 'activo' | 'vencido' | 'agotado' | 'revocado';
  /** Se puede volver a ver el comando (activo y guardado cifrado; los tokens anteriores no). */
  canShowCommand?: boolean;
}

export interface InstallerTokenInput {
  sucursalId: number;
  nombre?: string | null;
  diasVigencia: number;
  usosMax?: number | null;
  requiereAprobacion: boolean;
}

/** Token recién creado, o vuelto a mostrar con "Ver comando" (queda en la auditoría). */
export interface CreatedInstallerToken {
  id: number;
  token: string;
  vence: string;
  urlServidor: string;
  comando: string;
}

export interface PendingAgent {
  agentId: number;
  machineId: number;
  machineName: string;
  ipAddress: string | null;
  macAddress: string | null;
  operatingSystem: string | null;
  branchName: string;
  registeredAt: string;
}

// ------------------------------------------------------------------ alertas (W-20 a W-23)

export type AlertLevel = 'advertencia' | 'critico';
export type AlertStatus = 'nueva' | 'en_proceso' | 'resuelta';

export interface AlertFilter {
  q?: string;
  /** abiertas (por defecto), nueva, en_proceso, resuelta o todas. */
  estado?: string;
  nivel?: string;
  sucursal?: number | null;
  maquina?: number | null;
  /** true = solo las de red; false = solo las de equipos. */
  red?: boolean | null;
  pagina?: number;
  tamano?: number;
}

export interface AlertListItem {
  id: number;
  level: AlertLevel;
  status: AlertStatus;
  message: string;
  source: string | null;
  machineId: number | null;
  machineName: string | null;
  branchId: number | null;
  branchName: string | null;
  handledBy: string | null;
  createdAt: string;
  resolvedAt: string | null;
  /** La detectó el motor: se resuelve sola cuando el problema desaparece. */
  automatic: boolean;
  /** Alertas de red: el switch o router donde está el problema. */
  deviceId?: number | null;
  deviceName?: string | null;
}

export interface AlertCounts {
  open: number;
  new: number;
  inProgress: number;
  critical: number;
  warning: number;
  resolvedLast24h: number;
}

export interface AlertList {
  page: { items: AlertListItem[]; total: number; page: number; pageSize: number };
  counts: AlertCounts;
}

export interface AlertDetail {
  id: number;
  level: AlertLevel;
  status: AlertStatus;
  message: string;
  detail: string | null;
  advice: string | null;
  source: string | null;
  code: string | null;
  automatic: boolean;
  machineId: number | null;
  machineName: string | null;
  machineStatus: MachineStatus | null;
  branchId: number | null;
  branchName: string | null;
  ruleId: number | null;
  ruleName: string | null;
  handledBy: string | null;
  createdAt: string;
  resolvedAt: string | null;
  /** Veces que el mismo problema se abrió en el mismo equipo en 30 días (sin contar esta). */
  recurrences: number;
  history: { at: string; action: string; userName: string | null; note: string | null }[];
  deviceId?: number | null;
  deviceName?: string | null;
  portId?: number | null;
}

export interface RuleOverride {
  id: number;
  branchId: number;
  branchName: string;
  warning: number | null;
  critical: number | null;
  durationMinutes: number;
  isActive: boolean;
  autoRepair?: boolean;
}

export interface RuleItem {
  /** null = la empresa usa el valor por defecto. */
  id: number | null;
  metric: string;
  name: string;
  description: string;
  unit: string;
  lessIsWorse: boolean;
  hasThresholds: boolean;
  hasDuration: boolean;
  /** false = se evaluará cuando exista su módulo (impresoras, red). */
  available: boolean;
  warning: number | null;
  critical: number | null;
  durationMinutes: number;
  isActive: boolean;
  overrides: RuleOverride[];
  /** Servicio detenido: Centinela intenta iniciarlo solo. */
  autoRepair?: boolean;
  /** La métrica admite reparación automática (hoy, solo servicio detenido). */
  canAutoRepair?: boolean;
}

export interface RuleInput {
  metrica: string;
  /** null = toda la empresa. */
  sucursalId: number | null;
  umbralAdvertencia: number | null;
  umbralCritico: number | null;
  duracionMin: number;
  activa: boolean;
  /** Solo servicio detenido; sin indicar, no se cambia. */
  repararAuto?: boolean | null;
}

// ------------------------------------------------------------------ tickets (W-24 a W-26)

export type TicketPriority = 'baja' | 'media' | 'alta' | 'urgente';
export type TicketStatus = 'abierto' | 'en_proceso' | 'en_espera' | 'resuelto' | 'cerrado';

export interface TicketFilter {
  q?: string;
  /** activos (por defecto: todo menos cerrado), un estado o todos. */
  estado?: string;
  prioridad?: string;
  /** "yo", "sin" o el id de un usuario. */
  asignado?: string;
  sucursal?: number | null;
  maquina?: number | null;
  alerta?: number | null;
  pagina?: number;
  tamano?: number;
}

export interface TicketListItem {
  id: number;
  title: string;
  priority: TicketPriority;
  status: TicketStatus;
  machineId: number | null;
  machineName: string | null;
  branchName: string | null;
  alertId: number | null;
  assignedToId: number | null;
  assignedTo: string | null;
  createdBy: string;
  createdAt: string;
  closedAt: string | null;
  commentCount: number;
}

export interface TicketCounts {
  open: number;
  inProgress: number;
  onHold: number;
  resolved: number;
  closed: number;
  mine: number;
  unassigned: number;
}

export interface TicketList {
  page: { items: TicketListItem[]; total: number; page: number; pageSize: number };
  counts: TicketCounts;
}

export interface TicketDetail {
  id: number;
  title: string;
  description: string | null;
  priority: TicketPriority;
  status: TicketStatus;
  machineId: number | null;
  machineName: string | null;
  machineStatus: MachineStatus | null;
  branchId: number | null;
  branchName: string | null;
  alertId: number | null;
  alertMessage: string | null;
  alertStatus: AlertStatus | null;
  createdById: number;
  createdBy: string;
  assignedToId: number | null;
  assignedTo: string | null;
  createdAt: string;
  closedAt: string | null;
  comments: { id: number; userName: string; text: string; at: string }[];
  attachments: { id: number; contentType: string | null; at: string }[];
  history: { at: string; action: string; userName: string | null; detail: string | null }[];
}

export interface TicketInput {
  titulo: string;
  descripcion?: string | null;
  prioridad: TicketPriority;
  maquinaId?: number | null;
  alertaId?: number | null;
  asignadoA?: number | null;
}

export interface PanelApi {
  dashboard(): Promise<DashboardData>;
  machines(filter?: MachineFilter): Promise<MachineList>;
  machine(id: number): Promise<MachineDetail>;
  metrics(id: number, range: MetricRange): Promise<MetricPoint[]>;
  software(id: number): Promise<SoftwareItem[]>;
  interfaces(id: number): Promise<NetworkInterfaceItem[]>;
  events(id: number): Promise<ErrorEventItem[]>;
  processes(id: number): Promise<ProcessItem[]>;
  services(id: number): Promise<ServiceItem[]>;
  maintenances(filter: MaintenanceFilter): Promise<MaintenanceList>;
  saveMaintenance(id: number | null, input: MaintenanceInput): Promise<{ id: number }>;
  completeMaintenance(id: number, fecha?: string | null, nota?: string | null): Promise<{ id: number }>;
  deleteMaintenance(id: number): Promise<{ id: number }>;
  preferences(): Promise<UserPreferences>;
  savePreferences(input: UserPreferences): Promise<UserPreferences>;
  /** Switch, puerto y VLAN donde está el equipo (null si ningún switch lo ha visto). */
  networkConnection(id: number): Promise<MachineNetworkInfo | null>;
  history(id: number): Promise<AuditItem[]>;
  qr(id: number): Promise<MachineQr>;
  branches(): Promise<BranchItem[]>;
  branch(id: number): Promise<BranchItem>;
  createMachine(input: MachineInput): Promise<{ id: number }>;
  updateMachine(id: number, input: MachineInput): Promise<{ id: number }>;
  retireMachine(id: number): Promise<{ id: number }>;
  qrLabels(ids: number[]): Promise<QrLabel[]>;
  /** Qué es un código QR (enlaces monitoreoti://q/{codigo}); 404 si no existe o el usuario no lo ve. */
  resolveQr(code: string): Promise<QrTarget>;
  departments(): Promise<Option[]>;
  companyUsers(): Promise<Option[]>;
  createBranch(input: BranchInput): Promise<{ id: number }>;
  updateBranch(id: number, input: BranchInput): Promise<{ id: number }>;
  installers(): Promise<InstallerToken[]>;
  createInstaller(input: InstallerTokenInput): Promise<CreatedInstallerToken>;
  revokeInstaller(id: number): Promise<{ id: number }>;
  deleteInstaller(id: number): Promise<{ id: number }>;
  /** Vuelve a mostrar el token y el comando de instalación (409 si ya no está activo). */
  installerCommand(id: number): Promise<CreatedInstallerToken>;
  pendingAgents(): Promise<PendingAgent[]>;
  approveAgent(agentId: number): Promise<{ id: number }>;
  rejectAgent(agentId: number): Promise<{ id: number }>;
  alerts(filter?: AlertFilter): Promise<AlertList>;
  alert(id: number): Promise<AlertDetail>;
  attendAlert(id: number, note?: string | null): Promise<{ id: number }>;
  resolveAlert(id: number, note?: string | null): Promise<{ id: number }>;
  /** Inicia el servicio de una alerta de servicio detenido; devuelve el lote para seguir el resultado. */
  startAlertService(id: number): Promise<{ lote: string }>;
  rules(): Promise<RuleItem[]>;
  saveRule(input: RuleInput): Promise<{ id: number }>;
  deleteRule(id: number): Promise<{ id: number }>;
  tickets(filter?: TicketFilter): Promise<TicketList>;
  ticket(id: number): Promise<TicketDetail>;
  technicians(): Promise<{ id: number; name: string }[]>;
  createTicket(input: TicketInput): Promise<{ id: number }>;
  updateTicket(id: number, input: Pick<TicketInput, 'titulo' | 'descripcion' | 'prioridad'>): Promise<{ id: number }>;
  setTicketStatus(id: number, estado: TicketStatus): Promise<{ id: number }>;
  assignTicket(id: number, usuarioId: number | null): Promise<{ id: number }>;
  commentTicket(id: number, texto: string): Promise<{ id: number }>;
  /** Imagen como data URI ("data:image/png;base64,…"). */
  attachToTicket(id: number, tipo: string, datos: string): Promise<{ id: number }>;
  ticketAttachment(id: number, fileId: number): Promise<{ id: number; contentType: string; data: string }>;
  /** Data center (Fase 4). */
  red: NetworkApi;
  /** Gestión remota (Fase 6). */
  remoto: RemoteApi;
  /** Usuarios, roles y Mi perfil. */
  usuarios: UsersApi;
  configuracion: SettingsApi;
  /** Reportes (W-35). */
  reportes: ReportsApi;
}

/** El token de acceso se pide en cada llamada: se renueva solo y no debe quedar fijado en el cliente. */
export function createPanelApi(serverUrl: string, accessToken: () => string | null, fetchImpl?: FetchLike): PanelApi {
  const http = createHttp(serverUrl, fetchImpl);
  const get = <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) =>
    http<T>(`/api/v1${path}`, { accessToken: accessToken(), query });
  const send = <T>(method: string, path: string, body?: unknown) =>
    http<T>(`/api/v1${path}`, { method, body: body ?? {}, accessToken: accessToken() });

  return {
    dashboard: () => get('/dashboard'),
    machines: (filter = {}) => get('/maquinas', { ...filter }),
    machine: (id) => get(`/maquinas/${id}`),
    metrics: (id, range) => get(`/maquinas/${id}/metricas`, { rango: range }),
    software: (id) => get(`/maquinas/${id}/software`),
    interfaces: (id) => get(`/maquinas/${id}/interfaces`),
    events: (id) => get(`/maquinas/${id}/eventos`),
    processes: (id) => get(`/maquinas/${id}/procesos`),
    services: (id) => get(`/maquinas/${id}/servicios`),
    maintenances: (filter) => get('/mantenimientos', { ...filter }),
    saveMaintenance: (id, input) =>
      id === null ? send('POST', '/mantenimientos', input) : send('PUT', `/mantenimientos/${id}`, input),
    completeMaintenance: (id, fecha, nota) =>
      send('POST', `/mantenimientos/${id}/realizado`, { fecha: fecha ?? null, nota: nota ?? null }),
    deleteMaintenance: (id) => send('DELETE', `/mantenimientos/${id}`),
    preferences: () => get('/auth/preferencias'),
    savePreferences: (input) => send('PUT', '/auth/preferencias', input),
    networkConnection: (id) =>
      get<{ connection: MachineNetworkInfo | null }>(`/maquinas/${id}/conexion-red`).then((r) => r.connection),
    history: (id) => get(`/maquinas/${id}/historial`),
    qr: (id) => get(`/maquinas/${id}/qr`),
    branches: () => get('/sucursales'),
    branch: (id) => get(`/sucursales/${id}`),
    createMachine: (input) => send('POST', '/maquinas', input),
    updateMachine: (id, input) => send('PUT', `/maquinas/${id}`, input),
    retireMachine: (id) => send('POST', `/maquinas/${id}/baja`),
    qrLabels: (ids) => get('/maquinas/qr', { ids: ids.join(',') }),
    resolveQr: (code) => get(`/qr/${encodeURIComponent(code)}`),
    departments: () => get('/departamentos'),
    companyUsers: () => get('/usuarios/empresa'),
    createBranch: (input) => send('POST', '/sucursales', input),
    updateBranch: (id, input) => send('PUT', `/sucursales/${id}`, input),
    installers: () => get('/instaladores'),
    createInstaller: (input) => send('POST', '/instaladores', input),
    revokeInstaller: (id) => send('POST', `/instaladores/${id}/revocar`),
    installerCommand: (id) => get(`/instaladores/${id}/comando`),
    deleteInstaller: (id) => send('DELETE', `/instaladores/${id}`),
    pendingAgents: () => get('/equipos-pendientes'),
    approveAgent: (id) => send('POST', `/equipos-pendientes/${id}/aprobar`),
    rejectAgent: (id) => send('POST', `/equipos-pendientes/${id}/rechazar`),
    alerts: (filter = {}) => get('/alertas', { ...filter }),
    alert: (id) => get(`/alertas/${id}`),
    attendAlert: (id, note) => send('POST', `/alertas/${id}/atender`, { nota: note ?? null }),
    resolveAlert: (id, note) => send('POST', `/alertas/${id}/resolver`, { nota: note ?? null }),
    startAlertService: (id) => send('POST', `/alertas/${id}/iniciar-servicio`),
    rules: () => get('/reglas-alerta'),
    saveRule: (input) => send('PUT', '/reglas-alerta', input),
    deleteRule: (id) => send('DELETE', `/reglas-alerta/${id}`),
    tickets: (filter = {}) => get('/tickets', { ...filter }),
    ticket: (id) => get(`/tickets/${id}`),
    technicians: () => get('/tickets/tecnicos'),
    createTicket: (input) => send('POST', '/tickets', input),
    updateTicket: (id, input) => send('PUT', `/tickets/${id}`, input),
    setTicketStatus: (id, estado) => send('POST', `/tickets/${id}/estado`, { estado }),
    assignTicket: (id, usuarioId) => send('POST', `/tickets/${id}/asignar`, { usuarioId }),
    commentTicket: (id, texto) => send('POST', `/tickets/${id}/comentarios`, { texto }),
    attachToTicket: (id, tipo, datos) => send('POST', `/tickets/${id}/adjuntos`, { tipo, datos }),
    ticketAttachment: (id, fileId) => get(`/tickets/${id}/adjuntos/${fileId}`),
    red: createNetworkApi(get, send),
    remoto: createRemoteApi(get, send),
    usuarios: createUsersApi(get, send),
    configuracion: createSettingsApi(get, send),
    reportes: createReportsApi(get),
  };
}

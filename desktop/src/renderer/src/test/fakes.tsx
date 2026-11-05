import type { ReactNode } from 'react';
import { render } from '@testing-library/react';
import type {
  ReportsApi,
  AlertList,
  AlertListItem,
  AuthApi,
  CurrentSession,
  DashboardData,
  MachineDetail,
  MachineList,
  MachineListItem,
  NetworkApi,
  RemoteApi,
  PanelApi,
  StatusCounts,
  TicketList,
  TicketListItem,
  SettingsApi,
  UsersApi,
} from '@monitoreoti/shared';
import { SessionProvider } from '../auth/SessionProvider';
import { PanelDataProvider } from '../data/PanelData';

export const session: CurrentSession = {
  usuario: { id: 1, nombre: 'Ana Martínez', correo: 'admin@caribe.test', esSuperadmin: false },
  empresa: { id: 1, nombre: 'Grupo Comercial Caribe', rol: 'Administrador' },
  permisos: ['equipos.ver', 'dashboard.ver'],
  empresas: [{ id: 1, nombre: 'Grupo Comercial Caribe', rol: 'Administrador' }],
};

export const counts = (healthy = 2, warning = 1, critical = 1, disconnected = 1): StatusCounts => {
  const total = healthy + warning + critical + disconnected;
  return { total, healthy, warning, critical, disconnected, online: total - disconnected };
};

const now = new Date().toISOString();

export function machine(overrides: Partial<MachineListItem> = {}): MachineListItem {
  return {
    id: 1,
    name: 'CONTA-03',
    detail: 'Windows 11 Pro',
    branchId: 1,
    branchName: 'Santo Domingo',
    type: 'computadora',
    ipAddress: '10.10.20.31',
    status: 'critico',
    metrics: { at: now, cpu: 38, ram: 71, disk: 92, temperature: null },
    lastReportAt: now,
    ...overrides,
  };
}

export const machineList = (items: MachineListItem[]): MachineList => ({
  page: { items, total: items.length, page: 1, pageSize: 50 },
  counts: counts(),
  operatingSystems: ['Windows 11 Pro'],
});

export function machineDetail(overrides: Partial<MachineDetail> = {}): MachineDetail {
  return {
    id: 1,
    qrCode: '11111111-2222-3333-4444-555555555555',
    name: 'CONTA-03',
    type: 'computadora',
    status: 'critico',
    brand: 'Dell',
    model: 'OptiPlex 7010',
    serialNumber: 'DL7010-0003',
    operatingSystem: 'Windows 11 Pro',
    ipAddress: '10.10.20.31',
    macAddress: '3C:52:82:A1:00:03',
    assignedTo: 'Rosa Guzmán',
    purchaseDate: null,
    warrantyUntil: null,
    branchId: 1,
    branchName: 'Santo Domingo',
    departmentId: null,
    departmentName: 'Contabilidad',
    lastReportAt: now,
    createdAt: now,
    agentVersion: '0.1.0',
    hasAgent: true,
    metrics: { at: now, cpu: 38, ram: 71, disk: 92, temperature: 64 },
    components: [
      {
        id: 1,
        type: 'disco',
        description: 'SSD Samsung 512 GB',
        capacity: '477 GB',
        health: 'critico',
        detail: '148 sectores dañados · 41 °C',
        updatedAt: now,
      },
    ],
    problems: [
      {
        id: 9,
        level: 'critico',
        message: 'Disco con sectores dañados',
        detail:
          'El disco SSD Samsung 512 GB reporta: 148 sectores dañados · 41 °c. Puede perder archivos o dejar de arrancar.',
        advice: 'Respalda los datos hoy y programa el cambio del disco.',
        source: 'SMART · 148 sectores dañados · 41 °C',
        createdAt: now,
      },
    ],
    openErrorCount: 2,
    ramTotalMb: 16384,
    processCount: 142,
    volumes: [{ drive: 'C:', label: 'Sistema', totalGb: 476, freeGb: 39, updatedAt: now }],
    ...overrides,
  };
}

export const dashboard: DashboardData = {
  kpis: { counts: counts(214, 14, 3, 17), registeredThisWeek: 6, criticalWithOpenTicket: 2, disconnectedOver24h: 4 },
  fleetHealth: [{ date: now.slice(0, 10), healthy: 214, warning: 14, critical: 3, disconnected: 17 }],
  branches: [{ id: 1, name: 'Santo Domingo', province: null, counts: counts() }],
  recentAlerts: [],
  topProblems: [{ id: 1, name: 'CONTA-03', status: 'critico', summary: 'Disco con sectores dañados', events: 23 }],
};

export function alertItem(overrides: Partial<AlertListItem> = {}): AlertListItem {
  return {
    id: 41,
    level: 'critico',
    status: 'nueva',
    message: 'Disco C: al 97 % de su capacidad',
    source: 'Unidad C: · 97 / 100 GB',
    machineId: 1,
    machineName: 'CONTA-03',
    branchId: 1,
    branchName: 'Santo Domingo',
    handledBy: null,
    createdAt: now,
    resolvedAt: null,
    automatic: true,
    ...overrides,
  };
}

export function alertList(items: AlertListItem[]): AlertList {
  const open = items.filter((a) => a.status !== 'resuelta');
  return {
    page: { items, total: items.length, page: 1, pageSize: 50 },
    counts: {
      open: open.length,
      new: open.filter((a) => a.status === 'nueva').length,
      inProgress: open.filter((a) => a.status === 'en_proceso').length,
      critical: open.filter((a) => a.level === 'critico').length,
      warning: open.filter((a) => a.level === 'advertencia').length,
      resolvedLast24h: items.length - open.length,
    },
  };
}

export function ticketItem(overrides: Partial<TicketListItem> = {}): TicketListItem {
  return {
    id: 7,
    title: 'Cambiar disco de RRHH-02',
    priority: 'alta',
    status: 'abierto',
    machineId: 1,
    machineName: 'RRHH-02',
    branchName: 'Santo Domingo',
    alertId: null,
    assignedToId: null,
    assignedTo: null,
    createdBy: 'Ana Pérez',
    createdAt: now,
    closedAt: null,
    commentCount: 0,
    ...overrides,
  };
}

export function ticketList(items: TicketListItem[]): TicketList {
  const of = (s: TicketListItem['status']) => items.filter((t) => t.status === s).length;
  const active = items.filter((t) => t.status !== 'resuelto' && t.status !== 'cerrado');
  return {
    page: { items, total: items.length, page: 1, pageSize: 300 },
    counts: {
      open: of('abierto'),
      inProgress: of('en_proceso'),
      onHold: of('en_espera'),
      resolved: of('resuelto'),
      closed: of('cerrado'),
      mine: 0,
      unassigned: active.filter((t) => t.assignedToId === null).length,
    },
  };
}

/** API del data center falsa: listas vacías; el resto falla salvo lo que la prueba defina. */
export function fakeNetworkApi(overrides: Partial<NetworkApi> = {}): NetworkApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    rooms: () => Promise.resolve([]),
    racks: () => Promise.resolve([]),
    rack: missing,
    models: () => Promise.resolve([]),
    devices: () => Promise.resolve([]),
    device: missing,
    outlets: () => Promise.resolve([]),
    cables: () => Promise.resolve([]),
    path: missing,
    saveRoom: missing,
    deleteRoom: missing,
    saveRack: missing,
    deleteRack: missing,
    saveModel: missing,
    saveDevice: missing,
    placeDevice: missing,
    retireDevice: missing,
    pollDevice: missing,
    testDeviceApi: missing,
    installAgents: missing,
    remoteInstalls: () => Promise.resolve([]),
    savePort: missing,
    saveOutlet: missing,
    deleteOutlet: missing,
    createCable: missing,
    deleteCable: missing,
    history: () => Promise.resolve({ items: [], total: 0, page: 1, pageSize: 50 }),
    previewImport: missing,
    runImport: missing,
    summary: () =>
      Promise.resolve({
        devices: 0,
        online: 0,
        warning: 0,
        critical: 0,
        notResponding: 0,
        withoutCollector: 0,
        ports: 0,
        portsConnected: 0,
        portsWithProblems: 0,
        trunks: 0,
        unknownDevices: 0,
        differences: 0,
        openAlerts: 0,
        collectors: 0,
        collectorsOnline: 0,
        attention: [],
      }),
    collectors: () => Promise.resolve([]),
    unknown: () => Promise.resolve([]),
    differences: () => Promise.resolve([]),
    topology: () => Promise.resolve({ nodes: [], links: [] }),
    subnets: () => Promise.resolve([]),
    subnet: missing,
    saveSubnet: missing,
    deleteSubnet: missing,
    addReservation: missing,
    deleteReservation: missing,
    sweepSubnet: missing,
    ipHistory: () => Promise.resolve([]),
    search: () => Promise.resolve([]),
    assignPort: missing,
    ...overrides,
  };
}

/** API remota falsa: biblioteca y ejecuciones vacías; lo demás falla salvo lo que la prueba defina. */
export function fakeRemoteApi(overrides: Partial<RemoteApi> = {}): RemoteApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    scripts: () => Promise.resolve([]),
    saveScript: missing,
    deleteScript: missing,
    run: missing,
    runs: () => Promise.resolve([]),
    runDetail: missing,
    confirmTwoFactor: missing,
    openSession: missing,
    closeSession: missing,
    sessions: () => Promise.resolve({ items: [], total: 0, page: 1, pageSize: 50 }),
    tasks: () => Promise.resolve([]),
    saveTask: missing,
    deleteTask: missing,
    updates: missing,
    setAutoUpdate: missing,
    deployUpdate: missing,
    remoteSettings: () => Promise.resolve({ pedirPermiso: false }),
    saveRemoteSettings: missing,
    ...overrides,
  };
}

/** API de usuarios falsa: listas vacías; las escrituras fallan salvo lo que la prueba defina. */
export function fakeUsersApi(overrides: Partial<UsersApi> = {}): UsersApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    list: () => Promise.resolve([]),
    create: missing,
    update: missing,
    setActive: missing,
    passwordLink: missing,
    resetTwoFactor: missing,
    roles: () => Promise.resolve([]),
    permissions: () => Promise.resolve([]),
    saveRole: missing,
    deleteRole: missing,
    profile: missing,
    rename: missing,
    changePassword: missing,
    sessions: () => Promise.resolve([]),
    closeSession: missing,
    closeOtherSessions: missing,
    ...overrides,
  };
}

/** API de configuración falsa: sin logo ni color; las escrituras fallan salvo lo que la prueba defina. */
export function fakeSettingsApi(overrides: Partial<SettingsApi> = {}): SettingsApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    appearance: () => Promise.resolve({ color: null, logo: null }),
    company: missing,
    saveCompany: missing,
    setColor: missing,
    setLogo: missing,
    removeLogo: missing,
    departments: () => Promise.resolve([]),
    security: missing,
    saveSecurity: missing,
    email: missing,
    testEmail: missing,
    ...overrides,
  };
}

export function fakeReportsApi(overrides: Partial<ReportsApi> = {}): ReportsApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    info: () => Promise.resolve({ advanced: true }),
    inventory: missing,
    availability: missing,
    alerts: missing,
    diskSecurity: missing,
    performance: missing,
    software: missing,
    tickets: missing,
    remote: missing,
    ...overrides,
  };
}

/** API del panel falsa: cada método falla salvo los que la prueba defina. */
export function fakePanelApi(overrides: Partial<PanelApi>): PanelApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    red: fakeNetworkApi(),
    remoto: fakeRemoteApi(),
    usuarios: fakeUsersApi(),
    configuracion: fakeSettingsApi(),
    reportes: fakeReportsApi(),
    dashboard: missing,
    resolveQr: missing,
    machines: missing,
    machine: missing,
    metrics: () => Promise.resolve([]),
    software: missing,
    // La ficha consulta procesos y red desde el Resumen: vacíos salvo que la prueba los defina.
    interfaces: () => Promise.resolve([]),
    events: missing,
    processes: () => Promise.resolve([]),
    services: () => Promise.resolve([]),
    networkConnection: () => Promise.resolve(null),
    maintenances: () => Promise.resolve({ items: [], overdue: 0 }),
    saveMaintenance: missing,
    completeMaintenance: missing,
    deleteMaintenance: missing,
    preferences: () => Promise.resolve({ notificarCorreo: true }),
    savePreferences: missing,
    history: missing,
    qr: missing,
    branches: () => Promise.resolve([]),
    branch: missing,
    createMachine: missing,
    updateMachine: missing,
    retireMachine: missing,
    qrLabels: missing,
    departments: () => Promise.resolve([]),
    branchDepartments: () => Promise.resolve([]),
    saveDepartment: missing,
    deleteDepartment: missing,
    companyUsers: () => Promise.resolve([]),
    createBranch: missing,
    updateBranch: missing,
    installers: () => Promise.resolve([]),
    createInstaller: missing,
    revokeInstaller: missing,
    deleteInstaller: missing,
    installerCommand: missing,
    pendingAgents: () => Promise.resolve([]),
    approveAgent: missing,
    rejectAgent: missing,
    // La estructura pide el resumen de alertas para la insignia del menú.
    alerts: () => Promise.resolve(alertList([])),
    alert: missing,
    attendAlert: missing,
    resolveAlert: missing,
    startAlertService: missing,
    rules: () => Promise.resolve([]),
    saveRule: missing,
    deleteRule: missing,
    tickets: () => Promise.resolve(ticketList([])),
    ticket: missing,
    technicians: () => Promise.resolve([]),
    createTicket: missing,
    updateTicket: missing,
    setTicketStatus: missing,
    assignTicket: missing,
    commentTicket: missing,
    attachToTicket: missing,
    ticketAttachment: missing,
    ...overrides,
  };
}

const fakeAuth: AuthApi = {
  login: () => Promise.reject(new Error('no esperado')),
  verifyTwoFactor: () => Promise.reject(new Error('no esperado')),
  confirmTwoFactorSetup: () => Promise.reject(new Error('no esperado')),
  selectCompany: () => Promise.reject(new Error('no esperado')),
  refresh: () => Promise.reject(new Error('no esperado')),
  logout: () => Promise.resolve(),
  session: () => Promise.resolve(session),
  switchCompany: () => Promise.reject(new Error('no esperado')),
  requestPasswordReset: () => Promise.resolve(),
  resetPassword: () => Promise.resolve(),
};

/** Renderiza con sesión y datos falsos (sin servidor ni tiempo real). */
export function renderWithPanel(ui: ReactNode, api: PanelApi) {
  return render(
    <SessionProvider api={fakeAuth}>
      <PanelDataProvider api={api} realtime={false}>
        {ui}
      </PanelDataProvider>
    </SessionProvider>,
  );
}

/** Reportes (W-35). Los avanzados exigen el módulo reportes_avanzados: sin él, el servidor responde 403 modulo_no_incluido. */

/** Fechas yyyy-mm-dd (inclusive). Sin fechas: los últimos 30 días. */
export interface ReportFilter {
  desde?: string;
  hasta?: string;
  sucursal?: number;
  departamento?: number;
}

export interface CountItem {
  key: string;
  count: number;
}

export interface InventoryRow {
  id: number;
  name: string;
  type: string;
  branchName: string;
  departmentName: string | null;
  assignedTo: string | null;
  operatingSystem: string | null;
  processor: string | null;
  ramGb: number | null;
  disks: string | null;
  brand: string | null;
  model: string | null;
  serialNumber: string | null;
  ipAddress: string | null;
  macAddress: string | null;
  status: string;
  warrantyUntil: string | null;
  createdAt: string;
  lastReportAt: string | null;
}

export interface InventoryReport {
  total: number;
  byType: CountItem[];
  byOperatingSystem: CountItem[];
  rows: InventoryRow[];
}

export interface AvailabilityRow {
  id: number;
  name: string;
  branchName: string;
  hoursOnline: number;
  hoursInPeriod: number;
  percent: number;
  lastReportAt: string | null;
}

export interface AvailabilityReport {
  averagePercent: number;
  /** Foto diaria de toda la empresa: vacía si se ven solo algunas sucursales o un departamento. */
  days: { date: string; healthy: number; warning: number; critical: number; disconnected: number }[];
  machines: AvailabilityRow[];
}

export interface AlertReport {
  total: number;
  critical: number;
  warning: number;
  open: number;
  averageResolutionHours: number | null;
  perDay: { date: string; critical: number; warning: number }[];
  byCode: CountItem[];
  byBranch: CountItem[];
  topMachines: { id: number; name: string; branchName: string; count: number; critical: number }[];
}

export type SecurityIssue =
  'sin_antivirus' | 'antivirus_desactivado' | 'antivirus_desactualizado' | 'firewall_desactivado';

export interface DiskSecurityReport {
  lowDisks: {
    machineId: number;
    machineName: string;
    branchName: string;
    drive: string;
    totalGb: number;
    freeGb: number;
    freePercent: number;
  }[];
  diskHealth: {
    machineId: number;
    machineName: string;
    branchName: string;
    disk: string | null;
    health: string;
    detail: string | null;
  }[];
  security: {
    machineId: number;
    machineName: string;
    branchName: string;
    issue: SecurityIssue;
    detail: string | null;
  }[];
}

export type PerformanceAdvice = 'ampliar_ram' | 'revisar_cpu' | 'liberar_disco';

export interface PerformanceRow {
  id: number;
  name: string;
  branchName: string;
  cpuAverage: number | null;
  cpuPeak: number | null;
  ramAverage: number | null;
  ramPeak: number | null;
  diskMax: number | null;
  hours: number;
  advice: PerformanceAdvice[];
}

export interface PerformanceReport {
  trend: { date: string; cpu: number | null; ram: number | null }[];
  machines: PerformanceRow[];
}

export interface SoftwareReport {
  programs: number;
  rows: { name: string; publisher: string | null; machines: number; versions: CountItem[] }[];
  /** Solo al buscar un programa. */
  holders: {
    machineId: number;
    machineName: string;
    branchName: string;
    version: string | null;
    installedOn: string | null;
  }[];
}

export interface TicketReport {
  created: number;
  closed: number;
  open: number;
  averageResolutionHours: number | null;
  byPriority: CountItem[];
  technicians: { name: string; assigned: number; closed: number; averageResolutionHours: number | null }[];
  maintenance: { done: number; pending: number; overdue: number };
}

export interface RemoteReport {
  sessions: number;
  minutes: number;
  technicians: { name: string; sessions: number; minutes: number }[];
  rows: {
    id: number;
    startedAt: string;
    endedAt: string | null;
    minutes: number;
    userName: string;
    machineId: number;
    machineName: string;
    branchName: string;
    type: 'escritorio' | 'terminal' | 'archivos';
    viewOnly: boolean;
    reason: string | null;
  }[];
}

export interface ReportsApi {
  info(): Promise<{ advanced: boolean }>;
  inventory(filter?: ReportFilter): Promise<InventoryReport>;
  availability(filter?: ReportFilter): Promise<AvailabilityReport>;
  alerts(filter?: ReportFilter): Promise<AlertReport>;
  diskSecurity(filter?: ReportFilter): Promise<DiskSecurityReport>;
  performance(filter?: ReportFilter): Promise<PerformanceReport>;
  software(filter?: ReportFilter & { buscar?: string }): Promise<SoftwareReport>;
  tickets(filter?: ReportFilter): Promise<TicketReport>;
  remote(filter?: ReportFilter): Promise<RemoteReport>;
}

export function createReportsApi(
  get: <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) => Promise<T>,
): ReportsApi {
  return {
    info: () => get('/reportes'),
    inventory: (filter = {}) => get('/reportes/inventario', { ...filter }),
    availability: (filter = {}) => get('/reportes/disponibilidad', { ...filter }),
    alerts: (filter = {}) => get('/reportes/alertas', { ...filter }),
    diskSecurity: (filter = {}) => get('/reportes/discos-seguridad', { ...filter }),
    performance: (filter = {}) => get('/reportes/rendimiento', { ...filter }),
    software: (filter = {}) => get('/reportes/software', { ...filter }),
    tickets: (filter = {}) => get('/reportes/tickets', { ...filter }),
    remote: (filter = {}) => get('/reportes/remoto', { ...filter }),
  };
}

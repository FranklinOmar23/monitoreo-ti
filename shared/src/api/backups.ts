/**
 * Respaldos de servidores (decisión 82): los trabajos de cada servidor, su historial y lo que se le pregunta al agente.
 * Refleja Application/Backups/BackupContracts.cs; fechas en ISO 8601 UTC.
 */

export type BackupSource = 'sql' | 'carpetas';
export type BackupMode = 'caliente' | 'frio';
export type BackupStrategy = 'completo' | 'incremental';
export type BackupDestinationType = 'disco_externo' | 'red' | 'local';
export type BackupFrequency = 'diario' | 'semanal';
export type BackupRunStatus = 'en_curso' | 'correcto' | 'advertencia' | 'error';
export type BackupKind = 'completo' | 'incremental' | 'diferencial';

export interface BackupDisk {
  serie: string;
  nombre: string;
}

export interface BackupJobItem {
  id: number;
  name: string;
  source: BackupSource;
  sqlInstance: string | null;
  items: string[];
  mode: BackupMode;
  stopServices: string[];
  coldMaxMinutes: number;
  strategy: BackupStrategy;
  fullEveryDays: number;
  destinationType: BackupDestinationType;
  destinationPath: string;
  disks: BackupDisk[];
  ejectAfter: boolean;
  networkUser: string | null;
  hasNetworkPassword: boolean;
  frequency: BackupFrequency;
  /** 0 = domingo. */
  weekDays: number[];
  /** Hora local del servidor, HH:mm. */
  time: string;
  keepDaily: number;
  keepMonthly: number;
  verify: boolean;
  isActive: boolean;
  lastRunAt: string | null;
  lastStatus: BackupRunStatus | null;
  lastSuccessAt: string | null;
  lastError: string | null;
  nextRunAt: string | null;
  diskMissingSince: string | null;
  servicesNotRestarted: string | null;
  createdAt: string;
}

export interface BackupRunItem {
  id: number;
  jobId: number;
  jobName: string;
  trigger: 'programado' | 'manual';
  requestedBy: string | null;
  status: BackupRunStatus;
  kind: BackupKind | null;
  startedAt: string;
  finishedAt: string | null;
  bytesCopied: number;
  bytesTotal: number;
  files: number | null;
  destination: string | null;
  setName: string | null;
  detail: string | null;
}

export interface BackupOverview {
  machineId: number;
  machineName: string;
  isServer: boolean;
  typeLocked: boolean;
  agentVersion: string | null;
  agentConnected: boolean;
  agentSupportsBackups: boolean;
  okLast30Days: number;
  failedLast30Days: number;
  jobs: BackupJobItem[];
  runs: BackupRunItem[];
}

export interface BackupJobInput {
  nombre: string;
  origen: BackupSource;
  instanciaSql: string | null;
  elementos: string[];
  modo: BackupMode;
  serviciosDetener: string[] | null;
  frioMaxMinutos: number;
  estrategia: BackupStrategy;
  completoCadaDias: number;
  destinoTipo: BackupDestinationType;
  destinoRuta: string | null;
  discos: BackupDisk[] | null;
  expulsar: boolean;
  redUsuario: string | null;
  /** null = se conserva la clave guardada. */
  redClave: string | null;
  frecuencia: BackupFrequency;
  diasSemana: number[] | null;
  hora: string;
  retenerDiarios: number;
  retenerMensuales: number;
  verificar: boolean;
  activo: boolean;
}

export interface BackupDestinationInput {
  destinoTipo: BackupDestinationType;
  destinoRuta: string | null;
  discos: BackupDisk[] | null;
  redUsuario: string | null;
  redClave: string | null;
  /** Para probar con la clave guardada de ese trabajo si no se escribe otra. */
  trabajoId: number | null;
}

export interface BackupExploreResult {
  ok: boolean;
  error: string | null;
  instances: { name: string; error: string | null; databases: { name: string; sizeMb: number; recoveryModel: string | null }[] }[];
  services: { name: string; displayName: string; state: string }[];
  disks: { serial: string; name: string; letter: string | null; totalGb: number | null; freeGb: number | null }[];
}

export interface BackupDestinationResult {
  ok: boolean;
  error: string | null;
  freeGb: number | null;
  location: string | null;
}

export interface BackupsApi {
  overview(machineId: number): Promise<BackupOverview>;
  save(machineId: number, jobId: number | null, input: BackupJobInput): Promise<{ id: number }>;
  remove(machineId: number, jobId: number): Promise<{ id: number }>;
  runNow(machineId: number, jobId: number, full: boolean): Promise<{ id: number }>;
  explore(machineId: number): Promise<BackupExploreResult>;
  testDestination(machineId: number, input: BackupDestinationInput): Promise<BackupDestinationResult>;
}

export function createBackupsApi(
  get: <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) => Promise<T>,
  send: <T>(method: string, path: string, body?: unknown) => Promise<T>,
): BackupsApi {
  const base = (id: number) => `/maquinas/${id}/respaldos`;
  return {
    overview: (id) => get(base(id)),
    save: (id, jobId, input) => (jobId === null ? send('POST', base(id), input) : send('PUT', `${base(id)}/${jobId}`, input)),
    remove: (id, jobId) => send('DELETE', `${base(id)}/${jobId}`),
    runNow: (id, jobId, full) => send('POST', `${base(id)}/${jobId}/ahora`, { completo: full }),
    explore: (id) => send('POST', `${base(id)}/explorar`),
    testDestination: (id, input) => send('POST', `${base(id)}/probar-destino`, input),
  };
}

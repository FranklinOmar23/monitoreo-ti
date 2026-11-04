/** Gestión remota (Fase 6): biblioteca de scripts (W-33), ejecuciones (W-32) y confirmación del 2FA. */

export type ScriptLanguage = 'powershell' | 'cmd' | 'bash' | 'python';

export type CommandStatus = 'pendiente' | 'enviado' | 'ejecutando' | 'completado' | 'error' | 'expirado';

export interface ScriptItem {
  id: number;
  name: string;
  language: ScriptLanguage;
  description: string | null;
  content: string;
  createdBy: string;
  createdAt: string;
  updatedAt: string | null;
  /** Firmado con la llave de la empresa; sin firma válida el agente no lo ejecuta. */
  signed: boolean;
  lastRunAt: string | null;
}

export interface ScriptInput {
  nombre: string;
  lenguaje: ScriptLanguage;
  descripcion?: string | null;
  contenido: string;
}

/** Un script de la biblioteca (scriptId) o un comando escrito en el momento (lenguaje y contenido). */
export interface RunInput {
  maquinaIds: number[];
  scriptId?: number | null;
  lenguaje?: ScriptLanguage | null;
  contenido?: string | null;
  timeoutS?: number | null;
}

export interface RunSummary {
  batchId: string;
  title: string;
  language: ScriptLanguage | '';
  userName: string;
  createdAt: string;
  total: number;
  pending: number;
  running: number;
  completed: number;
  failed: number;
}

export interface CommandItem {
  id: number;
  batchId: string | null;
  machineId: number;
  machineName: string;
  branchName: string;
  status: CommandStatus;
  exitCode: number | null;
  createdAt: string;
  sentAt: string | null;
  completedAt: string | null;
  output: string | null;
}

export interface RunDetail {
  summary: RunSummary;
  content: string;
  timeoutSeconds: number;
  commands: CommandItem[];
}

export type SessionType = 'terminal' | 'archivos' | 'escritorio';

/** Para conectarse al relay: abrir un WebSocket a relayPath (en el servidor) y mandar {"token"} como primer mensaje. */
export interface SessionTicket {
  sessionId: number;
  type: SessionType;
  machineId: number;
  machineName: string;
  relayPath: string;
  token: string;
  expiresAt: string;
}

export interface SessionItem {
  id: number;
  type: SessionType;
  machineId: number;
  machineName: string;
  branchName: string;
  userId: number;
  userName: string;
  startedAt: string;
  endedAt: string | null;
  durationSeconds: number | null;
  sourceIp: string | null;
  active: boolean;
  reason: string | null;
  viewOnly: boolean;
}

/** Opciones del escritorio remoto elegidas antes de conectar (W-28a). */
export interface SessionOptions {
  motivo?: string | null;
  soloVer?: boolean;
  pedirPermiso?: boolean;
}

export interface SessionPage {
  items: SessionItem[];
  total: number;
  page: number;
  pageSize: number;
}

export const SESSION_TYPE_LABELS: Record<SessionType, string> = {
  terminal: 'Terminal',
  archivos: 'Archivos',
  escritorio: 'Escritorio',
};

export type TaskFrequency = 'una_vez' | 'diaria' | 'semanal' | 'mensual';
export type TaskTarget = 'equipos' | 'sucursal' | 'todos';

/** Tarea programada (6.5). Horas en hora de República Dominicana; fechas *At en UTC. */
export interface ScheduledTaskItem {
  id: number;
  name: string;
  scriptId: number;
  scriptName: string;
  target: TaskTarget;
  branchId: number | null;
  branchName: string | null;
  machineIds: number[];
  frequency: TaskFrequency;
  time: string;
  date: string | null;
  weekDays: number | null;
  monthDay: number | null;
  timeoutSeconds: number;
  active: boolean;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastBatchId: string | null;
  createdBy: string;
  /** "Todos los días a las 02:00", "Lunes y viernes a las 18:30"... */
  description: string;
}

export interface ScheduledTaskInput {
  nombre: string;
  scriptId: number;
  objetivo: TaskTarget;
  sucursalId?: number | null;
  maquinaIds?: number[] | null;
  frecuencia: TaskFrequency;
  /** "HH:mm" */
  hora: string;
  fecha?: string | null;
  /** lunes = 1, martes = 2, miércoles = 4 ... domingo = 64 */
  diasSemana?: number | null;
  diaMes?: number | null;
  timeoutS?: number | null;
  activa: boolean;
}

export const WEEK_DAYS: { bit: number; label: string; short: string }[] = [
  { bit: 1, label: 'Lunes', short: 'L' },
  { bit: 2, label: 'Martes', short: 'M' },
  { bit: 4, label: 'Miércoles', short: 'X' },
  { bit: 8, label: 'Jueves', short: 'J' },
  { bit: 16, label: 'Viernes', short: 'V' },
  { bit: 32, label: 'Sábado', short: 'S' },
  { bit: 64, label: 'Domingo', short: 'D' },
];

/** Actualización del agente (6.6). */
export interface AgentVersionItem {
  machineId: number;
  machineName: string;
  branchName: string;
  version: string;
  online: boolean;
  updating: boolean;
}

export interface UpdateStatus {
  /** Versión publicada; null si el servidor no tiene instalador o llave de firma (ver problem). */
  available: string | null;
  problem: string | null;
  autoUpdate: boolean;
  total: number;
  upToDate: number;
  outdated: number;
  updating: number;
  outdatedAgents: AgentVersionItem[];
}

export interface RemoteApi {
  scripts(): Promise<ScriptItem[]>;
  saveScript(id: number | null, input: ScriptInput): Promise<{ id: number }>;
  deleteScript(id: number): Promise<{ id: number }>;
  /** 403 con código "2fa_requerido" si el último 2FA tiene más de 8 horas: confirmar el código y reintentar. */
  run(input: RunInput): Promise<{ batchId: string; count: number }>;
  runs(machineId?: number): Promise<RunSummary[]>;
  runDetail(batchId: string): Promise<RunDetail>;
  confirmTwoFactor(codigo: string): Promise<{ confirmado: string; vigenteHasta: string }>;
  /** 409 si el equipo no está conectado; 403 "2fa_requerido" como run(). */
  openSession(maquinaId: number, tipo: SessionType, opciones?: SessionOptions): Promise<SessionTicket>;
  closeSession(id: number): Promise<{ id: number }>;
  sessions(filter?: { maquina?: number; pagina?: number; tamano?: number }): Promise<SessionPage>;
  tasks(): Promise<ScheduledTaskItem[]>;
  /** 403 "2fa_requerido" como run(). */
  saveTask(id: number | null, input: ScheduledTaskInput): Promise<{ id: number }>;
  deleteTask(id: number): Promise<{ id: number }>;
  updates(): Promise<UpdateStatus>;
  setAutoUpdate(activa: boolean): Promise<{ activa: boolean }>;
  deployUpdate(): Promise<{ ofrecidos: number }>;
  /** Si el usuario del equipo debe aceptar antes de que se vea su pantalla (por empresa). */
  remoteSettings(): Promise<{ pedirPermiso: boolean }>;
  saveRemoteSettings(input: { pedirPermiso: boolean }): Promise<{ pedirPermiso: boolean }>;
}

export const LANGUAGE_LABELS: Record<ScriptLanguage, string> = {
  powershell: 'PowerShell',
  cmd: 'CMD',
  bash: 'Bash',
  python: 'Python',
};

export const COMMAND_STATUS_LABELS: Record<CommandStatus, string> = {
  pendiente: 'Esperando al equipo',
  enviado: 'Enviado',
  ejecutando: 'Ejecutando',
  completado: 'Completado',
  error: 'Con error',
  expirado: 'Vencido',
};

export function createRemoteApi(
  get: <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) => Promise<T>,
  send: <T>(method: string, path: string, body?: unknown) => Promise<T>,
): RemoteApi {
  return {
    scripts: () => get('/scripts'),
    saveScript: (id, input) => (id === null ? send('POST', '/scripts', input) : send('PUT', `/scripts/${id}`, input)),
    deleteScript: (id) => send('DELETE', `/scripts/${id}`),
    run: (input) => send('POST', '/ejecuciones', input),
    runs: (machineId) => get('/ejecuciones', { maquina: machineId }),
    runDetail: (batchId) => get(`/ejecuciones/${batchId}`),
    confirmTwoFactor: (codigo) => send('POST', '/auth/confirmar-2fa', { codigo }),
    openSession: (maquinaId, tipo, opciones = {}) =>
      send('POST', '/sesiones-remotas', { maquinaId, tipo, ...opciones }),
    closeSession: (id) => send('POST', `/sesiones-remotas/${id}/cerrar`),
    sessions: (filter = {}) => get('/sesiones-remotas', { ...filter }),
    tasks: () => get('/tareas-programadas'),
    saveTask: (id, input) =>
      id === null ? send('POST', '/tareas-programadas', input) : send('PUT', `/tareas-programadas/${id}`, input),
    deleteTask: (id) => send('DELETE', `/tareas-programadas/${id}`),
    updates: () => get('/actualizaciones'),
    setAutoUpdate: (activa) => send('PUT', '/actualizaciones/automatica', { activa }),
    deployUpdate: () => send('POST', '/actualizaciones/desplegar'),
    remoteSettings: () => get('/sesiones-remotas/configuracion'),
    saveRemoteSettings: (input) => send('PUT', '/sesiones-remotas/configuracion', input),
  };
}

/**
 * Ticket que la ventana principal entrega para abrir una sesión en su propia ventana: la terminal (W-30) o el
 * escritorio remoto (W-28). Se valida antes de crear la ventana.
 */
export interface TerminalTicket {
  sessionId: number;
  /** terminal o escritorio (sin indicar: terminal). */
  kind: 'terminal' | 'escritorio';
  /** Equipo de la sesión (para pedir otra sesión del mismo equipo desde la ventana). */
  machineId?: number;
  relayPath: string;
  token: string;
  machineName: string;
  /** Opciones y datos que muestra el visor del escritorio (W-28, W-29). */
  quality?: number;
  clipboardSync?: boolean;
  viewOnly?: boolean;
  allowFiles?: boolean;
  technician?: string;
  branchName?: string;
  agentVersion?: string;
  lastReportAt?: string;
}

/** Calidad: 0 = automática, 35 rápida, 60 equilibrada, 85 alta. */
const QUALITIES = new Set([0, 35, 60, 85]);
const TEXTS = [
  ['technician', 150],
  ['branchName', 150],
  ['agentVersion', 40],
  ['lastReportAt', 40],
] as const;

export function parseTerminalTicket(value: unknown): TerminalTicket | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const sessionId = v['sessionId'];
  const relayPath = v['relayPath'];
  const token = v['token'];
  const machineName = v['machineName'];
  const kind = v['kind'] ?? 'terminal';
  if (kind !== 'terminal' && kind !== 'escritorio') return null;
  if (typeof sessionId !== 'number' || !Number.isInteger(sessionId) || sessionId <= 0) return null;
  if (typeof relayPath !== 'string' || relayPath !== `/relay/${sessionId}`) return null;
  if (typeof token !== 'string' || !/^[A-Za-z0-9_-]{20,100}$/.test(token)) return null;
  if (typeof machineName !== 'string' || machineName.length === 0 || machineName.length > 150) return null;
  const machineId = v['machineId'];
  if (machineId !== undefined && (typeof machineId !== 'number' || !Number.isInteger(machineId) || machineId <= 0))
    return null;

  const ticket: TerminalTicket = { sessionId, kind, relayPath, token, machineName };
  if (machineId !== undefined) ticket.machineId = machineId;
  if (typeof v['quality'] === 'number' && QUALITIES.has(v['quality'])) ticket.quality = v['quality'];
  for (const key of ['clipboardSync', 'viewOnly', 'allowFiles'] as const)
    if (typeof v[key] === 'boolean') ticket[key] = v[key];
  for (const [key, max] of TEXTS) {
    const text = v[key];
    if (typeof text === 'string' && text.length <= max) ticket[key] = text;
  }
  return ticket;
}

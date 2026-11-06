import { spawn } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { hostname } from 'node:os';
import { join } from 'node:path';
import { createEnvelope, type Envelope } from '@monitoreoti/shared';
import { verifySignature } from './executor';
import { hostJson, openSealed } from './snmp';

/**
 * Respaldos de servidores (decisión 82). El servidor manda los trabajos de este equipo firmados con la llave de la
 * empresa ("respaldos_config"); se verifican, se guardan en disco (así se siguen haciendo aunque falte la red) y se
 * ejecutan a su hora, de a uno, con el anfitrión C# ("respaldo"). Cada paso se informa con "respaldo_estado" y la
 * agenda (próxima ejecución y si está el disco) con "respaldos_agenda". La cuenta de la carpeta de red llega sellada
 * para este agente y solo se abre en memoria justo antes de usarla.
 */

export interface BackupDiskRef {
  serie: string;
  nombre: string;
}

export interface BackupDestinationConfig {
  tipo: 'disco_externo' | 'red' | 'local';
  ruta: string;
  discos: BackupDiskRef[];
  expulsar: boolean;
  /** Sellado para este agente: {usuario, clave}. */
  cuenta: string | null;
}

export interface BackupJobConfig {
  id: number;
  nombre: string;
  origen: 'sql' | 'carpetas';
  instancia_sql: string | null;
  elementos: string[];
  modo: 'caliente' | 'frio';
  servicios_detener: string[];
  frio_max_minutos: number;
  estrategia: 'completo' | 'incremental';
  completo_cada_dias: number;
  destino: BackupDestinationConfig;
  frecuencia: 'diario' | 'semanal';
  dias_semana: number[];
  hora: string;
  retener_diarios: number;
  retener_mensuales: number;
  verificar: boolean;
  activo: boolean;
}

export interface SignedBackupConfig {
  config: string;
  firma: string;
}

/** Una línea del anfitrión durante el respaldo. */
export interface BackupStep {
  estado: string;
  tipo?: string | null;
  detalle?: string | null;
  bytes_copiados?: number;
  bytes_totales?: number;
  archivos?: number | null;
  destino?: string | null;
  conjunto?: string | null;
  servicios_sin_arrancar?: string[] | null;
  fin?: boolean | null;
}

export interface BackupHost {
  run(request: unknown, onStep: (step: BackupStep) => void): Promise<void>;
  explore(): Promise<Record<string, unknown>>;
  disks(): Promise<{ ok: boolean; error?: string; discos?: { serie: string }[] }>;
  test(destination: unknown): Promise<{ ok: boolean; error?: string; libre_gb?: number | null; ubicacion?: string | null }>;
}

/** Texto firmado de la configuración (espejo de BackupRules.ConfigPayload del servidor). */
export function configPayload(machineId: number, config: string): string {
  return ['monitoreoti/respaldos/v1', machineId, createHash('sha256').update(config, 'utf8').digest('hex')].join('\n');
}

/** Texto firmado de "Respaldar ahora" (espejo de BackupRules.RunNowPayload). */
export function runNowPayload(machineId: number, jobId: number, runUid: string, full: boolean, expires: string): string {
  return ['monitoreoti/respaldo-ahora/v1', machineId, jobId, runUid, full ? 'completo' : 'normal', expires].join('\n');
}

const TIME = /^([01]\d|2[0-3]):([0-5]\d)$/;

/** ¿Ese día toca? Diario, todos; semanal, los elegidos (0 = domingo). */
function runsOn(job: BackupJobConfig, day: Date): boolean {
  return job.frecuencia !== 'semanal' || job.dias_semana.includes(day.getDay());
}

function at(day: Date, time: string): Date {
  const [, h, m] = TIME.exec(time) ?? ['', '23', '00'];
  const result = new Date(day);
  result.setHours(Number(h), Number(m), 0, 0);
  return result;
}

/** La última hora programada que ya pasó (hora local del servidor), o null si no hay en dos semanas. */
export function lastSlot(job: BackupJobConfig, now: Date): Date | null {
  for (let i = 0; i <= 14; i++) {
    const day = new Date(now);
    day.setDate(now.getDate() - i);
    const slot = at(day, job.hora);
    if (slot <= now && runsOn(job, slot)) return slot;
  }
  return null;
}

/** La próxima hora programada. */
export function nextSlot(job: BackupJobConfig, now: Date): Date | null {
  for (let i = 0; i <= 14; i++) {
    const day = new Date(now);
    day.setDate(now.getDate() + i);
    const slot = at(day, job.hora);
    if (slot > now && runsOn(job, slot)) return slot;
  }
  return null;
}

/** Si el servidor estuvo apagado a la hora, el respaldo se hace al volver, siempre que no hayan pasado más de 6 h. */
export const CATCH_UP_MS = 6 * 60 * 60_000;

function isJob(v: unknown): v is BackupJobConfig {
  if (!v || typeof v !== 'object') return false;
  const j = v as Record<string, unknown>;
  const d = j['destino'] as Record<string, unknown> | undefined;
  return (
    Number.isInteger(j['id']) &&
    typeof j['nombre'] === 'string' &&
    (j['origen'] === 'sql' || j['origen'] === 'carpetas') &&
    Array.isArray(j['elementos']) &&
    typeof j['hora'] === 'string' &&
    TIME.test(j['hora']) &&
    Array.isArray(j['dias_semana']) &&
    !!d &&
    typeof d['tipo'] === 'string' &&
    typeof d['ruta'] === 'string' &&
    Array.isArray(d['discos'])
  );
}

export interface BackupSchedulerOptions {
  machineId: number;
  credential: string;
  publicKey: () => string | null;
  dataDir: string;
  host: BackupHost;
  submit: (envelope: Envelope) => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  now?: () => Date;
}

interface QueuedRun {
  job: BackupJobConfig;
  runUid: string;
  trigger: 'programado' | 'manual';
  forceFull: boolean;
}

const CONFIG_FILE = 'respaldos.json';
const STATE_FILE = 'respaldos-estado.json';
const TICK_MS = 30_000;
const AGENDA_MS = 60 * 60_000;
/** El avance se informa como mucho cada 30 s (el anfitrión escribe cada 3 s). */
const PROGRESS_MS = 30_000;

export class BackupScheduler {
  private jobs: BackupJobConfig[] = [];
  /** Última hora programada que ya se hizo (o se dio por hecha) de cada trabajo. */
  private done: Record<string, string> = {};
  private readonly queue: QueuedRun[] = [];
  private running: QueuedRun | null = null;
  private timers: ReturnType<typeof setInterval>[] = [];

  constructor(private readonly options: BackupSchedulerOptions) {}

  private get now(): Date {
    return this.options.now?.() ?? new Date();
  }

  /** Arranca con lo guardado en disco (si la firma sigue siendo válida) y revisa los horarios cada 30 s. */
  start(): void {
    try {
      const saved = JSON.parse(readFileSync(join(this.options.dataDir, CONFIG_FILE), 'utf8')) as SignedBackupConfig;
      this.apply(saved, false);
    } catch {
      // Sin configuración guardada: llegará con la conexión.
    }
    try {
      this.done = JSON.parse(readFileSync(join(this.options.dataDir, STATE_FILE), 'utf8')) as Record<string, string>;
    } catch {
      this.done = {};
    }
    this.timers.push(setInterval(() => this.tick(), TICK_MS));
    this.timers.push(setInterval(() => void this.reportAgenda(), AGENDA_MS));
  }

  stop(): void {
    for (const timer of this.timers) clearInterval(timer);
    this.timers = [];
  }

  get jobCount(): number {
    return this.jobs.length;
  }

  /** "respaldos_config" del servidor. */
  configure(datos: unknown): void {
    const v = (datos ?? {}) as Record<string, unknown>;
    if (typeof v['config'] !== 'string' || typeof v['firma'] !== 'string') return;
    if (this.apply({ config: v['config'], firma: v['firma'] }, true)) void this.reportAgenda();
  }

  private apply(signed: SignedBackupConfig, persist: boolean): boolean {
    const key = this.options.publicKey();
    if (!key || !verifySignature(key, configPayload(this.options.machineId, signed.config), signed.firma)) {
      this.options.log('error', 'Configuración de respaldos con firma no válida: se ignora');
      return false;
    }
    let parsed: { maquina_id?: unknown; trabajos?: unknown };
    try {
      parsed = JSON.parse(signed.config) as typeof parsed;
    } catch {
      return false;
    }
    if (parsed.maquina_id !== this.options.machineId || !Array.isArray(parsed.trabajos)) return false;
    this.jobs = parsed.trabajos.filter(isJob);
    // Un trabajo nuevo empieza en su próxima hora (no se hace "la de ayer" al recibirlo).
    const now = this.now;
    for (const job of this.jobs) {
      if (this.done[job.id] === undefined) this.done[job.id] = (lastSlot(job, now) ?? now).toISOString();
    }
    this.saveState();
    if (persist) {
      try {
        writeFileSync(join(this.options.dataDir, CONFIG_FILE), JSON.stringify(signed));
      } catch (error) {
        this.options.log('error', 'No se pudo guardar la configuración de respaldos', { error: String(error) });
      }
    }
    this.options.log('info', 'Configuración de respaldos', { trabajos: this.jobs.length });
    return true;
  }

  private saveState(): void {
    try {
      writeFileSync(join(this.options.dataDir, STATE_FILE), JSON.stringify(this.done));
    } catch {
      // Sin estado guardado, a lo sumo se repite un respaldo tras reiniciar.
    }
  }

  /** Encola los respaldos cuya hora llegó (y los atrasados de menos de 6 h). */
  tick(): void {
    const now = this.now;
    for (const job of this.jobs) {
      if (!job.activo) continue;
      const slot = lastSlot(job, now);
      if (!slot) continue;
      const last = this.done[job.id] ? new Date(this.done[job.id]!) : null;
      if (last && slot <= last) continue;
      this.done[job.id] = slot.toISOString();
      this.saveState();
      if (now.getTime() - slot.getTime() > CATCH_UP_MS) {
        this.options.log('info', 'Respaldo omitido: el servidor estuvo apagado a esa hora', { trabajo: job.id });
        continue;
      }
      this.enqueue({ job, runUid: randomUUID(), trigger: 'programado', forceFull: false });
    }
  }

  /** "respaldo_ahora": solo si la firma es válida, no venció y el trabajo es de este equipo. */
  runNow(datos: unknown): void {
    const v = (datos ?? {}) as Record<string, unknown>;
    const key = this.options.publicKey();
    const jobId = v['trabajo_id'];
    const runUid = v['ejecucion'];
    const expires = v['vence'];
    if (
      !key ||
      v['maquina_id'] !== this.options.machineId ||
      typeof jobId !== 'number' ||
      typeof runUid !== 'string' ||
      typeof expires !== 'string' ||
      typeof v['firma'] !== 'string' ||
      !verifySignature(
        key,
        runNowPayload(this.options.machineId, jobId, runUid, v['completo'] === true, expires),
        v['firma'],
      )
    ) {
      this.options.log('error', 'Respaldar ahora con firma no válida: se ignora');
      return;
    }
    if (new Date(expires).getTime() < this.now.getTime()) return;
    const job = this.jobs.find((j) => j.id === jobId);
    if (!job) {
      this.report(jobId, runUid, 'manual', new Date(), {
        estado: 'error',
        detalle: 'El agente aún no tiene este trabajo: espere unos segundos e inténtelo de nuevo.',
        fin: true,
      });
      return;
    }
    this.enqueue({ job, runUid, trigger: 'manual', forceFull: v['completo'] === true });
  }

  private enqueue(run: QueuedRun): void {
    if (this.running?.job.id === run.job.id || this.queue.some((q) => q.job.id === run.job.id)) {
      this.report(run.job.id, run.runUid, run.trigger, this.now, {
        estado: 'error',
        detalle: 'Ese respaldo ya se está haciendo o está en cola.',
        fin: true,
      });
      return;
    }
    this.queue.push(run);
    void this.drain();
  }

  private async drain(): Promise<void> {
    if (this.running) return;
    const next = this.queue.shift();
    if (!next) return;
    this.running = next;
    try {
      await this.execute(next);
    } finally {
      this.running = null;
      void this.reportAgenda();
      void this.drain();
    }
  }

  private async execute(run: QueuedRun): Promise<void> {
    const started = this.now;
    const { job } = run;
    this.report(job.id, run.runUid, run.trigger, started, { estado: 'en_curso', detalle: 'Empezando…' });
    let account: { usuario?: string; clave?: string } = {};
    if (job.destino.cuenta) {
      try {
        account = openSealed(this.options.credential, job.destino.cuenta) as typeof account;
      } catch {
        this.report(job.id, run.runUid, run.trigger, started, {
          estado: 'error',
          detalle: 'No se pudo abrir la cuenta de la carpeta de red.',
          fin: true,
        });
        return;
      }
    }
    const request = {
      ejecucion: run.runUid,
      forzar_completo: run.forceFull,
      equipo: hostname(),
      trabajo: {
        ...job,
        destino: { ...job.destino, cuenta: undefined, usuario: account.usuario, clave: account.clave },
      },
    };
    this.options.log('info', 'Respaldo iniciado', { trabajo: job.id, disparo: run.trigger });
    let lastProgress = 0;
    let finished = false;
    await this.options.host.run(request, (step) => {
      if (step.fin) {
        finished = true;
        this.report(job.id, run.runUid, run.trigger, started, step);
        this.options.log(step.estado === 'error' ? 'error' : 'info', 'Respaldo terminado', {
          trabajo: job.id,
          estado: step.estado,
          tipo: step.tipo,
          detalle: step.detalle,
        });
        return;
      }
      const t = this.now.getTime();
      if (t - lastProgress < PROGRESS_MS) return;
      lastProgress = t;
      this.report(job.id, run.runUid, run.trigger, started, step);
    });
    if (!finished)
      this.report(job.id, run.runUid, run.trigger, started, {
        estado: 'error',
        detalle: 'El respaldo terminó sin resultado.',
        fin: true,
      });
  }

  private report(jobId: number, runUid: string, trigger: string, started: Date, step: BackupStep): void {
    this.options.submit(
      createEnvelope('respaldo_estado', {
        trabajoId: jobId,
        ejecucion: runUid,
        estado: step.estado,
        tipo: step.tipo ?? null,
        disparo: trigger,
        inicio: started.toISOString(),
        fin: step.fin ? this.now.toISOString() : null,
        bytesCopiados: step.bytes_copiados ?? 0,
        bytesTotales: step.bytes_totales ?? 0,
        archivos: step.archivos ?? null,
        destino: step.destino ?? null,
        conjunto: step.conjunto ?? null,
        detalle: step.detalle ?? null,
        serviciosSinArrancar: step.servicios_sin_arrancar ?? null,
      }),
    );
  }

  /** Próxima ejecución de cada trabajo y, en la hora previa, si hay alguno de sus discos conectado. */
  async reportAgenda(): Promise<void> {
    if (this.jobs.length === 0) return;
    const now = this.now;
    let connected: Set<string> | null = null;
    if (this.jobs.some((j) => j.activo && j.destino.tipo === 'disco_externo')) {
      const disks = await this.options.host.disks();
      if (disks.ok) connected = new Set((disks.discos ?? []).map((d) => d.serie.trim().toUpperCase()));
    }
    const trabajos = this.jobs.map((job) => {
      const next = job.activo ? nextSlot(job, now) : null;
      let discoConectado: boolean | null = null;
      if (job.activo && job.destino.tipo === 'disco_externo' && connected) {
        const has = job.destino.discos.some((d) => connected!.has(d.serie.trim().toUpperCase()));
        const soon = next !== null && next.getTime() - now.getTime() <= 60 * 60_000;
        discoConectado = has ? true : soon ? false : null;
      }
      return { trabajoId: job.id, proxima: next?.toISOString() ?? null, discoConectado };
    });
    this.options.submit(createEnvelope('respaldos_agenda', { trabajos }));
  }

  /** "respaldo_explorar": instancias y bases de SQL Server, servicios y discos externos. */
  async explore(datos: unknown): Promise<void> {
    const id = (datos as { solicitud_id?: unknown } | null)?.solicitud_id;
    if (typeof id !== 'string') return;
    const r = await this.options.host.explore();
    const list = (value: unknown) => (Array.isArray(value) ? (value as Record<string, unknown>[]) : []);
    this.options.submit(
      createEnvelope('respaldo_explorado', {
        solicitudId: id,
        ok: r['ok'] === true,
        error: r['error'] ?? null,
        instancias: list(r['instancias']).map((i) => ({
          nombre: i['nombre'],
          error: i['error'] ?? null,
          bases: list(i['bases']).map((b) => ({
            nombre: b['nombre'],
            tamanoMb: b['tamano_mb'] ?? 0,
            recuperacion: b['recuperacion'] ?? null,
          })),
        })),
        servicios: list(r['servicios']).map((s) => ({ nombre: s['nombre'], mostrar: s['mostrar'], estado: s['estado'] })),
        discos: list(r['discos']).map((d) => ({
          serie: d['serie'],
          nombre: d['nombre'],
          letra: d['letra'] ?? null,
          totalGb: d['total_gb'] ?? null,
          libreGb: d['libre_gb'] ?? null,
        })),
      }),
    );
  }

  /** "respaldo_probar": abre el destino con su cuenta y escribe un archivo de prueba. */
  async test(datos: unknown): Promise<void> {
    const v = (datos ?? {}) as Record<string, unknown>;
    const id = v['solicitud_id'];
    const destination = v['destino'] as BackupDestinationConfig | undefined;
    if (typeof id !== 'string' || !destination || typeof destination.tipo !== 'string') return;
    let account: { usuario?: string; clave?: string } = {};
    let result: { ok: boolean; error?: string; libre_gb?: number | null; ubicacion?: string | null };
    try {
      if (destination.cuenta) account = openSealed(this.options.credential, destination.cuenta) as typeof account;
      result = await this.options.host.test({ ...destination, cuenta: undefined, usuario: account.usuario, clave: account.clave });
    } catch {
      result = { ok: false, error: 'No se pudo abrir la cuenta de la carpeta de red.' };
    }
    this.options.log('info', 'Prueba del destino de respaldo', { tipo: destination.tipo, ok: result.ok, error: result.error });
    this.options.submit(
      createEnvelope('respaldo_prueba', {
        solicitudId: id,
        ok: result.ok,
        error: result.error ?? null,
        libreGb: result.libre_gb ?? null,
        ubicacion: result.ubicacion ?? null,
      }),
    );
  }
}

/** El anfitrión C# de verdad. */
export function hostBackups(hostExe: string | undefined): BackupHost {
  const missing = { ok: false as const, error: 'Falta el anfitrión del servicio en este equipo.' };
  return {
    run: (request, onStep) =>
      new Promise((resolve) => {
        if (!hostExe || !existsSync(hostExe)) {
          onStep({ estado: 'error', detalle: missing.error, fin: true });
          resolve();
          return;
        }
        const child = spawn(hostExe, ['respaldo'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
        let buffer = '';
        const line = (text: string) => {
          if (!text.trim()) return;
          try {
            const step = JSON.parse(text) as BackupStep;
            if (typeof step.estado === 'string') onStep(step);
          } catch {
            // Línea que no es JSON: se ignora.
          }
        };
        child.stdout.setEncoding('utf8').on('data', (chunk: string) => {
          buffer += chunk;
          let at: number;
          while ((at = buffer.indexOf('\n')) >= 0) {
            line(buffer.slice(0, at));
            buffer = buffer.slice(at + 1);
          }
        });
        child.on('error', (error) => {
          onStep({ estado: 'error', detalle: `No se pudo ejecutar el anfitrión del servicio: ${error.message}`, fin: true });
          resolve();
        });
        child.on('close', () => {
          line(buffer);
          resolve();
        });
        child.stdin.end(JSON.stringify(request));
      }),
    explore: () =>
      hostJson<{ ok: boolean; error?: string }>(hostExe, 'respaldo-explorar', {}, 60_000, 'Leer el servidor') as Promise<
        Record<string, unknown>
      >,
    disks: () => hostJson(hostExe, 'respaldo-discos', {}, 30_000, 'Leer los discos'),
    test: (destination) => hostJson(hostExe, 'respaldo-probar', destination, 60_000, 'Probar el destino'),
  };
}

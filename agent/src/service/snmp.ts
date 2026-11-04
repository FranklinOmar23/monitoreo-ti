import { spawn } from 'node:child_process';
import { createDecipheriv, createHash, hkdfSync } from 'node:crypto';
import { existsSync } from 'node:fs';
import { createEnvelope, type Envelope } from '@monitoreoti/shared';

/** Algo a consultar, tal como llega en "bienvenida" o "config_actualizada": un dispositivo de red o una impresora. */
export interface CollectorDevice {
  id: number;
  nombre: string;
  ip: string;
  tipo: string;
  usuario: string;
  /** Claves v3 o comunidad v2c, selladas para este agente (ver openCredential). */
  credencial: string;
  /** v3 (por defecto) o v2c (solo impresoras). */
  version?: string;
  /** dispositivo (switch, router…) o impresora (solo sus consumibles). */
  origen?: string;
}

/** Lo que el servidor selló: claves SNMPv3 o comunidad v2c. */
export type SnmpSecret = { auth: string; priv: string } | { community: string };

export interface CollectorConfig {
  intervalSeconds: number;
  devices: CollectorDevice[];
}

/** Lo que pide el agente al anfitrión C# ("MonitoreoTI.AgentHost.exe snmp"). */
export interface SnmpQueryRequest {
  ip: string;
  version?: 'v2c' | 'v3';
  usuario?: string;
  claveAutenticacion?: string;
  clavePrivacidad?: string;
  comunidad?: string;
  consultas?: string[];
}

/** Lo que devuelve el anfitrión (y se manda tal cual al servidor, con el id del dispositivo). */
export interface SnmpQueryResult {
  ok: boolean;
  error?: string;
  [key: string]: unknown;
}

export type SnmpQuery = (request: SnmpQueryRequest) => Promise<SnmpQueryResult>;

/** Lee la configuración del recolector de un mensaje del servidor; null si no es recolector. */
export function parseCollectorConfig(value: unknown): CollectorConfig | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const devices = Array.isArray(v['dispositivos']) ? v['dispositivos'] : [];
  const valid = devices.filter(
    (d): d is CollectorDevice =>
      !!d &&
      typeof d === 'object' &&
      typeof (d as CollectorDevice).id === 'number' &&
      typeof (d as CollectorDevice).ip === 'string' &&
      typeof (d as CollectorDevice).usuario === 'string' &&
      typeof (d as CollectorDevice).credencial === 'string',
  );
  if (valid.length === 0) return null;
  const seconds = typeof v['intervalo_s'] === 'number' && v['intervalo_s'] >= 60 ? v['intervalo_s'] : 300;
  return { intervalSeconds: seconds, devices: valid };
}

/**
 * Abre las claves que el servidor selló para este agente: AES-256-GCM con una llave HKDF-SHA256 derivada del
 * SHA-256 de la credencial del agente (espejo de SnmpCredentialSeal en el servidor). Solo vive en memoria.
 */
export function openCredential(agentCredential: string, sealed: string): SnmpSecret {
  const raw = Buffer.from(sealed, 'base64');
  const hash = createHash('sha256').update(agentCredential, 'utf8').digest('hex');
  const key = Buffer.from(hkdfSync('sha256', Buffer.from(hash, 'hex'), Buffer.alloc(0), 'monitoreoti/snmp/v1', 32));
  const decipher = createDecipheriv('aes-256-gcm', key, raw.subarray(0, 12));
  decipher.setAuthTag(raw.subarray(raw.length - 16));
  const plain = Buffer.concat([decipher.update(raw.subarray(12, raw.length - 16)), decipher.final()]).toString('utf8');
  const parsed = JSON.parse(plain) as { auth?: unknown; priv?: unknown; community?: unknown };
  if (typeof parsed.community === 'string') return { community: parsed.community };
  if (typeof parsed.auth !== 'string' || typeof parsed.priv !== 'string')
    throw new Error('Credencial SNMP incompleta.');
  return { auth: parsed.auth, priv: parsed.priv };
}

/**
 * Consulta con el anfitrión C#: la petición (con las claves) va por la entrada estándar, nunca en la línea de
 * comandos, y el resultado vuelve en JSON por la salida estándar.
 */
export function hostSnmpQuery(hostExe: string | undefined, timeoutMs = 120_000): SnmpQuery {
  return (request) => {
    if (!hostExe || !existsSync(hostExe))
      return Promise.resolve({
        ok: false,
        error: 'El recolector SNMP no está disponible en este equipo (falta el anfitrión del servicio).',
      });

    return new Promise((resolve) => {
      const child = spawn(hostExe, ['snmp'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      let stdout = '';
      const timer = setTimeout(() => {
        child.kill();
        resolve({ ok: false, error: 'La consulta SNMP tardó demasiado.' });
      }, timeoutMs);
      child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
      child.on('error', (error) => {
        clearTimeout(timer);
        resolve({ ok: false, error: `No se pudo ejecutar la consulta SNMP: ${error.message}` });
      });
      child.on('close', () => {
        clearTimeout(timer);
        try {
          resolve(JSON.parse(stdout) as SnmpQueryResult);
        } catch {
          resolve({ ok: false, error: 'La consulta SNMP no devolvió un resultado válido.' });
        }
      });
      child.stdin.end(JSON.stringify(request));
    });
  };
}

export interface SnmpPollerOptions {
  credential: string;
  query: SnmpQuery;
  submit: (envelope: Envelope) => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  /** Primer sondeo tras recibir la lista (los siguientes, cada intervalo). */
  firstDelayMs?: number;
}

/**
 * Recolector SNMP (sección 11.1): consulta cada dispositivo asignado en su intervalo, de a uno (no satura la red ni
 * el equipo), y manda cada resultado al servidor por la cola local. Las claves se abren justo antes de cada consulta.
 */
export class SnmpPoller {
  private config: CollectorConfig | null = null;
  private timers: ReturnType<typeof setTimeout>[] = [];
  private queue: Promise<void> = Promise.resolve();
  private generation = 0;

  constructor(private readonly options: SnmpPollerOptions) {}

  get deviceCount(): number {
    return this.config?.devices.length ?? 0;
  }

  /** Reemplaza la lista de dispositivos (bienvenida o config_actualizada); null deja de consultar. */
  configure(config: CollectorConfig | null): void {
    this.clear();
    this.config = config;
    const generation = ++this.generation;
    if (!config) return;

    this.options.log('info', 'Recolector SNMP activo', { dispositivos: config.devices.length });
    const everyMs = config.intervalSeconds * 1000;
    const first = this.options.firstDelayMs ?? 15_000;
    config.devices.forEach((device, index) => {
      // Escalonados dentro del intervalo para no consultar todo a la vez.
      const offset = first + Math.floor((everyMs / Math.max(config.devices.length, 1)) * index);
      const tick = () => {
        if (generation !== this.generation) return;
        this.enqueue(device, generation);
        this.timers.push(setTimeout(tick, everyMs));
      };
      this.timers.push(setTimeout(tick, offset));
    });
  }

  stop(): void {
    this.clear();
    this.config = null;
    this.generation++;
  }

  /** Consulta un dispositivo ahora (lo usa la programación y las pruebas). */
  poll(device: CollectorDevice): Promise<void> {
    return this.enqueue(device, this.generation);
  }

  private enqueue(device: CollectorDevice, generation: number): Promise<void> {
    this.queue = this.queue.then(() => (generation === this.generation ? this.pollNow(device) : undefined));
    return this.queue;
  }

  private async pollNow(device: CollectorDevice): Promise<void> {
    const printer = device.origen === 'impresora';
    let result: SnmpQueryResult;
    try {
      const secret = openCredential(this.options.credential, device.credencial);
      // De una impresora solo interesan sus consumibles (tóner).
      const consultas = printer ? ['consumibles'] : undefined;
      result = await this.options.query(
        'community' in secret
          ? { ip: device.ip, version: 'v2c', comunidad: secret.community, consultas }
          : {
              ip: device.ip,
              version: 'v3',
              usuario: device.usuario,
              claveAutenticacion: secret.auth,
              clavePrivacidad: secret.priv,
              consultas,
            },
      );
    } catch {
      // Sin detalles: el error podría venir de las claves.
      result = { ok: false, error: 'No se pudieron abrir las claves SNMP del dispositivo.' };
    }
    if (!result.ok)
      this.options.log('error', 'Sondeo SNMP fallido', { dispositivo: device.nombre, error: result.error });
    this.options.submit(
      createEnvelope(
        'snmp_resultado',
        printer ? { ...result, maquinaId: device.id } : { ...result, dispositivoId: device.id },
      ),
    );
  }

  private clear(): void {
    for (const timer of this.timers) clearTimeout(timer);
    this.timers = [];
  }
}

import { spawn } from 'node:child_process';
import { createDecipheriv, createHash, hkdfSync } from 'node:crypto';
import { existsSync } from 'node:fs';
import { createEnvelope, type Envelope } from '@monitoreoti/shared';
import {
  collectFortiGate,
  fortiHttpGet,
  parseApiConfig,
  readFortiSwitches,
  switchToSnmpResult,
  type FortiApiConfig,
  type FortiHttp,
} from './fortigate';

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
  /** API REST del FortiGate (decisión 76), si tiene token. */
  api?: FortiApiConfig;
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
  for (const device of valid) {
    const api = parseApiConfig((device as unknown as Record<string, unknown>)['api']);
    if (api) device.api = api;
    else delete device.api;
  }
  const seconds = typeof v['intervalo_s'] === 'number' && v['intervalo_s'] >= 60 ? v['intervalo_s'] : 300;
  return { intervalSeconds: seconds, devices: valid };
}

/**
 * Abre las claves que el servidor selló para este agente: AES-256-GCM con una llave HKDF-SHA256 derivada del
 * SHA-256 de la credencial del agente (espejo de SnmpCredentialSeal en el servidor). Solo vive en memoria.
 */
export function openCredential(agentCredential: string, sealed: string): SnmpSecret {
  const parsed = openSealed(agentCredential, sealed) as { auth?: unknown; priv?: unknown; community?: unknown };
  if (typeof parsed.community === 'string') return { community: parsed.community };
  if (typeof parsed.auth !== 'string' || typeof parsed.priv !== 'string')
    throw new Error('Credencial SNMP incompleta.');
  return { auth: parsed.auth, priv: parsed.priv };
}

/** Abre un JSON sellado por el servidor para este agente (claves SNMP, token de la API). */
export function openSealed(agentCredential: string, sealed: string): unknown {
  const raw = Buffer.from(sealed, 'base64');
  const hash = createHash('sha256').update(agentCredential, 'utf8').digest('hex');
  const key = Buffer.from(hkdfSync('sha256', Buffer.from(hash, 'hex'), Buffer.alloc(0), 'monitoreoti/snmp/v1', 32));
  const decipher = createDecipheriv('aes-256-gcm', key, raw.subarray(0, 12));
  decipher.setAuthTag(raw.subarray(raw.length - 16));
  const plain = Buffer.concat([decipher.update(raw.subarray(12, raw.length - 16)), decipher.final()]).toString('utf8');
  return JSON.parse(plain) as unknown;
}

/** El token de la API sellado como {token}. */
export function openApiToken(agentCredential: string, sealed: string): string {
  const parsed = openSealed(agentCredential, sealed) as { token?: unknown };
  if (typeof parsed?.token !== 'string' || parsed.token === '') throw new Error('Token de API incompleto.');
  return parsed.token;
}

/**
 * Pide algo al anfitrión C# ("MonitoreoTI.AgentHost.exe <comando>"): la petición va por la entrada estándar (con las
 * claves, si las hay), nunca en la línea de comandos, y el resultado vuelve en JSON por la salida estándar.
 */
export function hostJson<T extends { ok: boolean; error?: string }>(
  hostExe: string | undefined,
  command: string,
  request: unknown,
  timeoutMs: number,
  what: string,
): Promise<T | { ok: false; error: string }> {
  if (!hostExe || !existsSync(hostExe))
    return Promise.resolve({
      ok: false,
      error: `${what} no está disponible en este equipo (falta el anfitrión del servicio).`,
    });

  return new Promise((resolve) => {
    const child = spawn(hostExe, [command], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '';
    const timer = setTimeout(() => {
      child.kill();
      resolve({ ok: false, error: `${what} tardó demasiado.` });
    }, timeoutMs);
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
    child.on('error', (error) => {
      clearTimeout(timer);
      resolve({ ok: false, error: `No se pudo ejecutar el anfitrión del servicio: ${error.message}` });
    });
    child.on('close', () => {
      clearTimeout(timer);
      try {
        resolve(JSON.parse(stdout) as T);
      } catch {
        resolve({ ok: false, error: `${what} no devolvió un resultado válido.` });
      }
    });
    child.stdin.end(JSON.stringify(request));
  });
}

/** Consulta SNMP con el anfitrión C#. */
export function hostSnmpQuery(hostExe: string | undefined, timeoutMs = 120_000): SnmpQuery {
  return (request) => hostJson<SnmpQueryResult>(hostExe, 'snmp', request, timeoutMs, 'La consulta SNMP');
}

export interface SnmpPollerOptions {
  credential: string;
  query: SnmpQuery;
  submit: (envelope: Envelope) => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  /** Primer sondeo tras recibir la lista (los siguientes, cada intervalo). */
  firstDelayMs?: number;
  /** Cliente HTTP de la API del FortiGate (las pruebas lo reemplazan). */
  fortiHttp?: FortiHttp;
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

  /** "Sondear ahora" desde el panel: consulta ese dispositivo sin esperar su turno; false si no lo tiene asignado. */
  pollById(deviceId: number): boolean {
    const device = this.config?.devices.find((d) => d.id === deviceId && d.origen !== 'impresora');
    if (!device) return false;
    void this.enqueue(device, this.generation);
    return true;
  }

  /** Consulta un dispositivo ahora (lo usa la programación y las pruebas). */
  poll(device: CollectorDevice): Promise<void> {
    return this.enqueue(device, this.generation);
  }

  /**
   * Tabla ARP de un dispositivo que este recolector consulta (para el barrido de IP, decisión 72); null si no lo tiene
   * asignado o no responde. Va por la misma cola que los sondeos.
   */
  arpOf(deviceId: number): Promise<{ ip: string; mac: string }[] | null> {
    const device = this.config?.devices.find((d) => d.id === deviceId && d.origen !== 'impresora');
    if (!device) return Promise.resolve(null);
    let arp: { ip: string; mac: string }[] | null = null;
    this.queue = this.queue.then(async () => {
      try {
        const secret = openCredential(this.options.credential, device.credencial);
        if ('community' in secret) return;
        const result = await this.options.query({
          ip: device.ip,
          version: 'v3',
          usuario: device.usuario,
          claveAutenticacion: secret.auth,
          clavePrivacidad: secret.priv,
          consultas: ['arp'],
        });
        if (result.ok && Array.isArray(result['arp'])) arp = result['arp'] as { ip: string; mac: string }[];
      } catch {
        // Claves que no abren: el barrido sigue sin esta ARP.
      }
    });
    return this.queue.then(() => arp);
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
    if (device.api && !printer) await this.readApi(device, device.api);
  }

  /** Después del SNMP, lo que da la API REST (DHCP y dispositivos). El token no se escribe en el registro. */
  private async readApi(device: CollectorDevice, api: FortiApiConfig): Promise<void> {
    let datos: Record<string, unknown>;
    let token: string | undefined;
    try {
      token = openApiToken(this.options.credential, api.token);
      datos = {
        dispositivoId: device.id,
        ...(await collectFortiGate(this.options.fortiHttp ?? fortiHttpGet, {
          url: api.url,
          token,
          acceptSelfSigned: api.acceptSelfSigned,
        })),
      };
    } catch {
      datos = { dispositivoId: device.id, ok: false, error: 'No se pudo abrir el token de la API.' };
    }
    if (datos['ok'] !== true)
      this.options.log('error', 'Lectura de la API fallida', { dispositivo: device.nombre, error: datos['error'] });
    this.options.submit(createEnvelope('fortigate_datos', datos));
    if (token && api.switches?.length) await this.readSwitches(device, api, token);
  }

  /** Los FortiSwitch de este FortiGate (decisión 80): un "snmp_resultado" por cada uno, con lo que da la API. */
  private async readSwitches(device: CollectorDevice, api: FortiApiConfig, token: string): Promise<void> {
    const read = await readFortiSwitches(this.options.fortiHttp ?? fortiHttpGet, {
      url: api.url,
      token,
      acceptSelfSigned: api.acceptSelfSigned,
    });
    if (!read.ok)
      this.options.log('error', 'Lectura de los FortiSwitch fallida', {
        dispositivo: device.nombre,
        error: read.error,
      });
    for (const registered of api.switches ?? []) {
      const sw = read.switches.find((s) => s.serie.toUpperCase() === registered.serie.toUpperCase());
      this.options.submit(
        createEnvelope(
          'snmp_resultado',
          switchToSnmpResult(registered.id, registered.serie, sw, device.nombre, read.ok ? undefined : read.error),
        ),
      );
    }
  }

  private clear(): void {
    for (const timer of this.timers) clearTimeout(timer);
    this.timers = [];
  }
}

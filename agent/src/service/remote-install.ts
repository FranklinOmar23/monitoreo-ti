import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream, existsSync } from 'node:fs';
import { mkdir, readdir, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { createEnvelope, type Envelope } from '@monitoreoti/shared';
import { openSealed } from './snmp';
import { downloadRelease, verifyUpdate, type UpdateOffer } from './updater';

/**
 * Instalación remota del agente (decisión 77): el servidor pide "instalar_agente" con la IP, la cuenta del dominio y un
 * token de registro (los dos sellados para este agente) y el instalador publicado (versión, SHA-256 y firma). El
 * recolector verifica la firma, descarga el instalador una vez (queda en caché) y le pasa todo al anfitrión C#, que va
 * informando cada paso. La clave solo vive en memoria y nunca va al registro.
 */

export interface RemoteInstallRequest {
  installId: number;
  ip: string;
  /** Sellado: {usuario, clave}. */
  account: string;
  server: string;
  /** Sellado: {token}. */
  token: string;
  offer: UpdateOffer;
}

/** Lo que recibe el anfitrión por stdin. */
export interface HostInstallRequest {
  ip: string;
  usuario: string;
  clave: string;
  instalador: string;
  ca?: string;
  servidor: string;
  token: string;
}

export interface InstallStep {
  estado: string;
  detalle?: string;
  sistema?: string;
  fin?: boolean;
}

export type HostInstall = (request: HostInstallRequest, onStep: (step: InstallStep) => void) => Promise<void>;

const IPV4 = /^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$/;

export function parseInstallRequest(value: unknown): RemoteInstallRequest | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const offer = v['instalador'] as Record<string, unknown> | undefined;
  if (
    typeof v['instalacion_id'] !== 'number' ||
    typeof v['ip'] !== 'string' ||
    !IPV4.test(v['ip']) ||
    typeof v['cuenta'] !== 'string' ||
    typeof v['servidor'] !== 'string' ||
    typeof v['token'] !== 'string' ||
    !offer ||
    typeof offer['version'] !== 'string' ||
    offer['url'] !== '/agent/actualizacion' ||
    typeof offer['sha256'] !== 'string' ||
    !/^[0-9a-f]{64}$/.test(offer['sha256']) ||
    typeof offer['tamano'] !== 'number' ||
    typeof offer['firma'] !== 'string'
  )
    return null;
  return {
    installId: v['instalacion_id'],
    ip: v['ip'],
    account: v['cuenta'],
    server: v['servidor'],
    token: v['token'],
    offer: offer as unknown as UpdateOffer,
  };
}

/**
 * El anfitrión C# ("instalar-remoto"): la petición va por stdin y cada línea de stdout es un paso en JSON. Si termina sin
 * el paso final, se informa como error.
 */
export function hostRemoteInstall(hostExe: string | undefined, timeoutMs = 40 * 60_000): HostInstall {
  return (request, onStep) =>
    new Promise((resolve) => {
      if (!hostExe || !existsSync(hostExe)) {
        onStep({ estado: 'error', detalle: 'Falta el anfitrión del servicio en el recolector.', fin: true });
        resolve();
        return;
      }
      const child = spawn(hostExe, ['instalar-remoto'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      let finished = false;
      let buffer = '';
      const timer = setTimeout(() => child.kill(), timeoutMs);
      const line = (text: string) => {
        if (!text.trim()) return;
        try {
          const step = JSON.parse(text) as InstallStep;
          if (typeof step.estado !== 'string') return;
          if (step.fin) finished = true;
          onStep(step);
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
      const done = (problem?: string) => {
        clearTimeout(timer);
        line(buffer);
        if (!finished)
          onStep({ estado: 'error', detalle: problem ?? 'La instalación terminó sin resultado.', fin: true });
        resolve();
      };
      child.on('error', (error) => done(`No se pudo ejecutar el anfitrión del servicio: ${error.message}`));
      child.on('close', () => done());
      child.stdin.end(JSON.stringify(request));
    });
}

async function sha256Of(file: string): Promise<string> {
  const hash = createHash('sha256');
  await new Promise<void>((resolve, reject) => {
    createReadStream(file)
      .on('data', (chunk) => hash.update(chunk))
      .on('end', resolve)
      .on('error', reject);
  });
  return hash.digest('hex');
}

export interface RemoteInstallerOptions {
  serverUrl: URL;
  credential: string;
  dataDir: string;
  /** El certificado de confianza del servidor (el mismo que usa este agente) para copiarlo al equipo nuevo. */
  caFile?: string;
  install: HostInstall;
  submit: (envelope: Envelope) => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  fetchImpl?: typeof fetch;
  publicKey?: string;
}

export class RemoteInstaller {
  private queue: Promise<void> = Promise.resolve();

  constructor(private readonly options: RemoteInstallerOptions) {}

  get cacheDir(): string {
    return join(this.options.dataDir, 'instalador');
  }

  /** De a una: no se copian 150 MB a varios equipos a la vez. */
  handle(datos: unknown): Promise<void> {
    const request = parseInstallRequest(datos);
    if (!request) {
      this.options.log('error', 'Pedido de instalación remota con formato inválido; se descarta');
      return Promise.resolve();
    }
    this.queue = this.queue.then(() => this.run(request));
    return this.queue;
  }

  private report(id: number, step: InstallStep): void {
    this.options.submit(
      createEnvelope('instalacion_estado', {
        instalacionId: id,
        estado: step.estado,
        detalle: step.detalle ?? null,
        sistema: step.sistema ?? null,
      }),
    );
  }

  private async run(request: RemoteInstallRequest): Promise<void> {
    const fail = (detalle: string) => this.report(request.installId, { estado: 'error', detalle, fin: true });
    if (!verifyUpdate(request.offer, this.options.publicKey)) {
      fail('El instalador ofrecido no está firmado por el producto.');
      return;
    }

    let installer: string;
    try {
      installer = await this.installer(request.offer);
    } catch (error) {
      fail(`No se pudo descargar el instalador: ${error instanceof Error ? error.message : String(error)}`);
      return;
    }

    let usuario: string, clave: string, token: string;
    try {
      const account = openSealed(this.options.credential, request.account) as { usuario?: unknown; clave?: unknown };
      const sealedToken = openSealed(this.options.credential, request.token) as { token?: unknown };
      if (
        typeof account.usuario !== 'string' ||
        typeof account.clave !== 'string' ||
        typeof sealedToken.token !== 'string'
      )
        throw new Error();
      ({ usuario, clave } = account as { usuario: string; clave: string });
      token = sealedToken.token;
    } catch {
      fail('No se pudieron abrir la cuenta o el token de la instalación.');
      return;
    }

    this.options.log('info', 'Instalación remota del agente', { ip: request.ip, cuenta: usuario });
    let last: InstallStep | undefined;
    await this.options.install(
      {
        ip: request.ip,
        usuario,
        clave,
        instalador: installer,
        ca: this.options.caFile && existsSync(this.options.caFile) ? this.options.caFile : undefined,
        servidor: request.server,
        token,
      },
      (step) => {
        last = step;
        this.report(request.installId, step);
      },
    );
    this.options.log(last?.estado === 'error' ? 'error' : 'info', 'Instalación remota terminada', {
      ip: request.ip,
      estado: last?.estado,
      detalle: last?.detalle,
    });
  }

  /** El instalador de esa versión, verificado; se descarga una sola vez y se conserva solo el último. */
  private async installer(offer: UpdateOffer): Promise<string> {
    await mkdir(this.cacheDir, { recursive: true });
    const file = join(this.cacheDir, `MonitoreoTI-Agente-Setup-${offer.version}.exe`);
    if (existsSync(file) && (await sha256Of(file)) === offer.sha256) return file;
    for (const old of await readdir(this.cacheDir)) await rm(join(this.cacheDir, old), { force: true });
    await downloadRelease({
      serverUrl: this.options.serverUrl,
      credential: this.options.credential,
      offer,
      file,
      fetchImpl: this.options.fetchImpl,
    });
    return file;
  }
}

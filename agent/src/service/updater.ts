import { createHash, createPublicKey, verify } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { mkdir, readdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { Readable } from 'node:stream';
import type { ReadableStream as WebReadableStream } from 'node:stream/web';

/**
 * Llave pública del producto (decisión 53): firma las versiones del agente. La privada vive solo en el servidor.
 * El anfitrión C# tiene la misma y vuelve a verificar antes de instalar.
 */
export const PRODUCT_UPDATE_KEY =
  'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvv7LSrnvcjzHw7jVrgZnrWnKQ5Ang7yAlQltWZherSm1atJSMIgU8bTA2U2wD3uYg489JoNM1brmc0nYSQZyQA==';

/** Código con el que el agente termina para que el anfitrión instale la versión descargada. */
export const UPDATE_EXIT_CODE = 75;

/** Tope de tamaño del instalador (el actual ronda los 150 MB). */
const MAX_BYTES = 600 * 1024 * 1024;

export interface UpdateOffer {
  version: string;
  url: string;
  sha256: string;
  tamano: number;
  firma: string;
}

export function updatePayload(version: string, sha256: string): string {
  return ['monitoreoti/actualizacion/v1', version, sha256].join('\n');
}

export function verifyUpdate(
  offer: Pick<UpdateOffer, 'version' | 'sha256' | 'firma'>,
  publicKey = PRODUCT_UPDATE_KEY,
): boolean {
  try {
    const key = createPublicKey({ key: Buffer.from(publicKey, 'base64'), format: 'der', type: 'spki' });
    return verify(
      'sha256',
      Buffer.from(updatePayload(offer.version, offer.sha256), 'utf8'),
      { key, dsaEncoding: 'ieee-p1363' },
      Buffer.from(offer.firma, 'base64'),
    );
  } catch {
    return false;
  }
}

/** "1.2.10" > "1.2.9" por números. */
export function isNewer(candidate: string, current: string): boolean {
  const parts = (v: string) =>
    v
      .split(/[-+]/)[0]!
      .split('.')
      .map((p) => Number.parseInt(p, 10) || 0);
  const a = parts(candidate);
  const b = parts(current);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const diff = (a[i] ?? 0) - (b[i] ?? 0);
    if (diff !== 0) return diff > 0;
  }
  return false;
}

export function parseOffer(value: unknown): UpdateOffer | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  return typeof v['version'] === 'string' &&
    /^\d+(\.\d+){1,3}([-+][\w.]+)?$/.test(v['version']) &&
    v['url'] === '/agent/actualizacion' &&
    typeof v['sha256'] === 'string' &&
    /^[0-9a-f]{64}$/.test(v['sha256']) &&
    typeof v['tamano'] === 'number' &&
    v['tamano'] > 0 &&
    v['tamano'] <= MAX_BYTES &&
    typeof v['firma'] === 'string'
    ? (v as unknown as UpdateOffer)
    : null;
}

export interface UpdaterOptions {
  serverUrl: URL;
  credential: string;
  currentVersion: string;
  dataDir: string;
  /** Termina el agente con UPDATE_EXIT_CODE para que el anfitrión instale. */
  handOver: () => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  fetchImpl?: typeof fetch;
  publicKey?: string;
}

/**
 * Actualizador (sección 8.1): ante "actualizar_agente" verifica la firma del producto, descarga el instalador con su
 * credencial, comprueba tamaño y SHA-256 y deja el pedido en update-pending.json; luego termina para que el anfitrión
 * (que vuelve a verificar) lo instale en silencio, conservando la configuración y la credencial.
 */
export class AgentUpdater {
  private busy = false;

  constructor(private readonly options: UpdaterOptions) {}

  get updatesDir(): string {
    return join(this.options.dataDir, 'updates');
  }

  async handle(datos: unknown): Promise<void> {
    const offer = parseOffer(datos);
    if (!offer) {
      this.options.log('error', 'Oferta de actualización con formato inválido; se descarta');
      return;
    }
    if (this.busy || !isNewer(offer.version, this.options.currentVersion)) return;
    if (!verifyUpdate(offer, this.options.publicKey)) {
      this.options.log('error', 'Actualización rechazada: la firma no es del producto', { version: offer.version });
      return;
    }

    this.busy = true;
    try {
      const file = await this.download(offer);
      await writeFile(
        join(this.options.dataDir, 'update-pending.json'),
        JSON.stringify({ version: offer.version, file, sha256: offer.sha256, firma: offer.firma }),
      );
      this.options.log('info', 'Actualización descargada; el anfitrión la instalará', {
        desde: this.options.currentVersion,
        a: offer.version,
      });
      this.options.handOver();
    } catch (error) {
      this.options.log('error', 'No se pudo descargar la actualización', {
        version: offer.version,
        error: String(error),
      });
    } finally {
      this.busy = false;
    }
  }

  private async download(offer: UpdateOffer): Promise<string> {
    // Solo se guarda la última descarga.
    await mkdir(this.updatesDir, { recursive: true });
    for (const old of await readdir(this.updatesDir)) await rm(join(this.updatesDir, old), { force: true });

    const response = await (this.options.fetchImpl ?? fetch)(new URL(offer.url, this.options.serverUrl), {
      headers: { Authorization: `Bearer ${this.options.credential}` },
    });
    if (!response.ok || !response.body) throw new Error(`El servidor respondió ${response.status}.`);

    const file = join(this.updatesDir, `MonitoreoTI-Agente-${offer.version}.exe`);
    const hash = createHash('sha256');
    let size = 0;
    await new Promise<void>((resolve, reject) => {
      const out = createWriteStream(file);
      const body = Readable.fromWeb(response.body as unknown as WebReadableStream<Uint8Array>);
      body.on('data', (chunk: Buffer) => {
        size += chunk.length;
        if (size > offer.tamano) body.destroy(new Error('El archivo es más grande de lo anunciado.'));
        hash.update(chunk);
      });
      body.on('error', reject);
      out.on('error', reject);
      out.on('finish', resolve);
      body.pipe(out);
    });

    const digest = hash.digest('hex');
    if (size !== offer.tamano || digest !== offer.sha256) {
      await rm(file, { force: true });
      throw new Error('El archivo descargado no coincide con el publicado (tamaño o SHA-256).');
    }
    return file;
  }
}

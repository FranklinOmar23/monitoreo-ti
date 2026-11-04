import { createReadStream, type ReadStream } from 'node:fs';
import { access, mkdir, open, readdir, rename, rm, stat, type FileHandle } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import type { SessionChannel, SessionHandler } from './remote-sessions';

/**
 * Explorador de archivos remoto (sección 10.2, W-31) sobre el relay. Pedidos del panel en texto con un id:
 *   {op:"listar", id, ruta}            ruta "" = unidades
 *   {op:"descargar", id, ruta}         respuesta {id, ok, tamano}; luego partes binarias y {id, fin:true}
 *   {op:"ack", id}                     el panel recibió una parte (como mucho WINDOW sin confirmar)
 *   {op:"subir", id, ruta, tamano, reemplazar?}   luego partes binarias; el agente responde {id, recibido}
 *   {op:"subir_fin", id}               cierra y deja el archivo con su nombre final
 *   {op:"cancelar", id}
 *   {op:"crear_carpeta"|"eliminar", id, ruta}   {op:"renombrar", id, ruta, nombre}
 * Partes binarias: [id:4, big endian][hasta 256 KB]. Respuestas: {id, ok:true, ...} o {id, ok:false, error}.
 * Corre como SYSTEM: ve todo el disco. El contenido nunca se registra; cada operación se informa para la auditoría.
 */

export const CHUNK_BYTES = 256 * 1024;
export const WINDOW = 4;
const MAX_ENTRIES = 5000;
const PART_SUFFIX = '.monitoreoti-parte';

export interface FileEntry {
  nombre: string;
  tipo: 'carpeta' | 'archivo' | 'unidad';
  tamano: number | null;
  modificado: string | null;
}

/** Lo que se informa al servidor (auditoría) de cada operación con efecto o descarga. */
export interface FileActivity {
  accion: 'descargar' | 'subir' | 'crear_carpeta' | 'renombrar' | 'eliminar';
  ruta: string;
  destino?: string;
  tamano?: number;
}

type Request = Record<string, unknown> & { op: string; id: number };

interface Download {
  stream: ReadStream;
  pending: number;
}

interface Upload {
  handle: FileHandle;
  target: string;
  part: string;
  size: number;
  received: number;
}

export function frame(id: number, data: Uint8Array): Uint8Array {
  const out = new Uint8Array(4 + data.length);
  new DataView(out.buffer).setUint32(0, id);
  out.set(data, 4);
  return out;
}

export function filesHandlerFactory(report: (sessionId: number, activity: FileActivity) => void) {
  return (channel: SessionChannel): SessionHandler =>
    new FileSession(channel, (activity) => report(channel.sessionId, activity));
}

class FileSession implements SessionHandler {
  private readonly downloads = new Map<number, Download>();
  private readonly uploads = new Map<number, Upload>();

  constructor(
    private readonly channel: SessionChannel,
    private readonly report: (activity: FileActivity) => void,
  ) {}

  onText(text: string): void {
    let request: Request;
    try {
      request = JSON.parse(text) as Request;
    } catch {
      return;
    }
    if (typeof request.op !== 'string' || !Number.isInteger(request.id)) return;
    void this.handle(request).catch((error: unknown) => this.reply(request.id, { ok: false, error: describe(error) }));
  }

  onBinary(data: Uint8Array): void {
    if (data.length < 4) return;
    const id = new DataView(data.buffer, data.byteOffset, data.byteLength).getUint32(0);
    const upload = this.uploads.get(id);
    if (!upload) return;
    const chunk = data.subarray(4);
    if (upload.received + chunk.length > upload.size) {
      void this.abortUpload(id, 'El archivo recibido es más grande de lo anunciado.');
      return;
    }
    upload.received += chunk.length;
    // Se escribe en orden: cada escritura espera a la anterior por la cadena de promesas del manejador.
    this.queue = this.queue
      .then(() => upload.handle.write(chunk))
      .then(() => this.reply(id, { recibido: upload.received }))
      .catch((error: unknown) => this.abortUpload(id, describe(error)));
  }

  private queue: Promise<unknown> = Promise.resolve();

  dispose(): void {
    for (const download of this.downloads.values()) download.stream.destroy();
    this.downloads.clear();
    for (const id of [...this.uploads.keys()]) void this.abortUpload(id, null);
  }

  private async handle(r: Request): Promise<void> {
    switch (r.op) {
      case 'listar':
        return this.list(r.id, String(r['ruta'] ?? ''));
      case 'descargar':
        return this.download(r.id, checkedPath(r['ruta']));
      case 'ack': {
        const download = this.downloads.get(r.id);
        if (download && download.pending > 0 && --download.pending < WINDOW) download.stream.resume();
        return;
      }
      case 'subir':
        return this.startUpload(r.id, checkedPath(r['ruta']), Number(r['tamano']), r['reemplazar'] === true);
      case 'subir_fin':
        return this.finishUpload(r.id);
      case 'cancelar':
        this.downloads.get(r.id)?.stream.destroy();
        this.downloads.delete(r.id);
        if (this.uploads.has(r.id)) await this.abortUpload(r.id, null);
        return;
      case 'crear_carpeta': {
        const path = checkedPath(r['ruta']);
        await mkdir(path);
        this.report({ accion: 'crear_carpeta', ruta: path });
        return this.reply(r.id, { ok: true });
      }
      case 'renombrar': {
        const path = checkedPath(r['ruta']);
        const name = String(r['nombre'] ?? '');
        if (!name || name !== basename(name) || /[<>:"/\\|?*]/.test(name))
          throw new Error('El nombre nuevo no es válido.');
        const target = join(dirname(path), name);
        if (await exists(target)) throw new Error('Ya existe algo con ese nombre.');
        await rename(path, target);
        this.report({ accion: 'renombrar', ruta: path, destino: target });
        return this.reply(r.id, { ok: true });
      }
      case 'eliminar': {
        const path = checkedPath(r['ruta']);
        if (resolve(path) === resolve(dirname(path))) throw new Error('No se puede eliminar una unidad completa.');
        await rm(path, { recursive: true, force: false });
        this.report({ accion: 'eliminar', ruta: path });
        return this.reply(r.id, { ok: true });
      }
      default:
        return this.reply(r.id, { ok: false, error: `Operación desconocida: ${r.op}` });
    }
  }

  private async list(id: number, path: string): Promise<void> {
    if (path === '') return this.reply(id, { ok: true, ruta: '', entradas: await drives() });

    const folder = checkedPath(path);
    const items = await readdir(folder, { withFileTypes: true });
    const entries: FileEntry[] = [];
    for (const item of items.slice(0, MAX_ENTRIES)) {
      // Lo que no se puede leer (en uso, sin permiso, enlace roto) se muestra sin tamaño.
      let size: number | null = null;
      let modified: string | null = null;
      try {
        const info = await stat(join(folder, item.name));
        size = info.isFile() ? info.size : null;
        modified = info.mtime.toISOString();
      } catch {
        // Se deja sin datos.
      }
      entries.push({
        nombre: item.name,
        tipo: item.isDirectory() ? 'carpeta' : 'archivo',
        tamano: size,
        modificado: modified,
      });
    }
    this.reply(id, { ok: true, ruta: folder, entradas: entries, recortado: items.length > MAX_ENTRIES });
  }

  private async download(id: number, path: string): Promise<void> {
    const info = await stat(path);
    if (!info.isFile()) throw new Error('Solo se pueden descargar archivos.');
    this.reply(id, { ok: true, tamano: info.size });
    this.report({ accion: 'descargar', ruta: path, tamano: info.size });

    const stream = createReadStream(path, { highWaterMark: CHUNK_BYTES });
    const download: Download = { stream, pending: 0 };
    this.downloads.set(id, download);
    stream.on('data', (chunk: Buffer | string) => {
      const bytes = typeof chunk === 'string' ? Buffer.from(chunk) : chunk;
      this.channel.sendBinary(frame(id, bytes));
      // Control de flujo: como mucho WINDOW partes sin confirmar en camino.
      if (++download.pending >= WINDOW) stream.pause();
    });
    stream.on('end', () => {
      this.downloads.delete(id);
      this.reply(id, { fin: true });
    });
    stream.on('error', (error) => {
      this.downloads.delete(id);
      this.reply(id, { ok: false, error: describe(error) });
    });
  }

  private async startUpload(id: number, target: string, size: number, replace: boolean): Promise<void> {
    if (!Number.isSafeInteger(size) || size < 0) throw new Error('Tamaño inválido.');
    if (!replace && (await exists(target))) throw new Error('Ya existe un archivo con ese nombre.');
    const part = `${target}${PART_SUFFIX}`;
    const handle = await open(part, 'w');
    this.uploads.set(id, { handle, target, part, size, received: 0 });
    this.reply(id, { ok: true });
  }

  private async finishUpload(id: number): Promise<void> {
    const upload = this.uploads.get(id);
    if (!upload) throw new Error('No hay una subida en curso con ese id.');
    await this.queue;
    this.uploads.delete(id);
    await upload.handle.close();
    if (upload.received !== upload.size) {
      await rm(upload.part, { force: true });
      throw new Error(`Llegaron ${upload.received} de ${upload.size} bytes; no se guardó.`);
    }
    await rm(upload.target, { force: true });
    await rename(upload.part, upload.target);
    this.report({ accion: 'subir', ruta: upload.target, tamano: upload.size });
    this.reply(id, { ok: true, fin: true });
  }

  private async abortUpload(id: number, error: string | null): Promise<void> {
    const upload = this.uploads.get(id);
    if (!upload) return;
    this.uploads.delete(id);
    await upload.handle.close().catch(() => undefined);
    await rm(upload.part, { force: true }).catch(() => undefined);
    if (error) this.reply(id, { ok: false, error });
  }

  private reply(id: number, data: Record<string, unknown>): void {
    this.channel.sendText(JSON.stringify({ id, ...data }));
  }
}

function checkedPath(value: unknown): string {
  if (typeof value !== 'string' || !isAbsolute(value))
    throw new Error('La ruta debe ser completa (por ejemplo C:\\Users).');
  return resolve(value);
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

/** Unidades de Windows presentes (A: a Z:). */
async function drives(): Promise<FileEntry[]> {
  const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
  const found = await Promise.all(letters.map(async (l) => ((await exists(`${l}:\\`)) ? `${l}:\\` : null)));
  return found
    .filter((d): d is string => d !== null)
    .map((d) => ({ nombre: d, tipo: 'unidad', tamano: null, modificado: null }));
}

function describe(error: unknown): string {
  const code = (error as NodeJS.ErrnoException | null)?.code;
  switch (code) {
    case 'ENOENT':
      return 'No existe.';
    case 'EACCES':
    case 'EPERM':
      return 'Sin permiso (o el archivo está en uso).';
    case 'EBUSY':
      return 'El archivo está en uso.';
    case 'ENOTEMPTY':
      return 'La carpeta no está vacía.';
    case 'EEXIST':
      return 'Ya existe.';
    case 'ENOSPC':
      return 'No hay espacio en el disco.';
    default:
      return error instanceof Error ? error.message : String(error);
  }
}

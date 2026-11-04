/** Cliente del explorador de archivos remoto sobre el relay (protocolo en agent/src/service/files.ts). */

export const CHUNK_BYTES = 256 * 1024;
export const WINDOW = 4;

export interface RemoteEntry {
  nombre: string;
  tipo: 'carpeta' | 'archivo' | 'unidad';
  tamano: number | null;
  modificado: string | null;
}

export interface Listing {
  ruta: string;
  entradas: RemoteEntry[];
  recortado?: boolean;
}

type Reply = Record<string, unknown> & { id: number };

interface Waiter {
  /** Cada respuesta con ese id pasa por aquí; true si ya terminó. */
  onReply: (reply: Reply) => boolean;
  onBinary?: (data: Uint8Array) => void;
  fail: (error: Error) => void;
}

/** Dónde se guarda lo descargado (el proceso principal escribe en el archivo elegido). */
export interface DownloadSink {
  write(data: Uint8Array): Promise<void>;
  end(ok: boolean): Promise<void>;
}

/** De dónde se lee lo que se sube. */
export interface UploadSource {
  size: number;
  read(offset: number, length: number): Promise<Uint8Array>;
}

export class RemoteError extends Error {}

export class FileClient {
  private nextId = 1;
  private readonly waiters = new Map<number, Waiter>();

  /**
   * @param prefix Marcador de las partes binarias cuando el explorador va dentro de otra sesión (el escritorio usa 2:
   * sus imágenes empiezan con 1). Sin marcador, todo binario es una parte de archivo.
   */
  constructor(
    private readonly socket: WebSocket,
    private readonly prefix?: number,
  ) {
    socket.addEventListener('message', this.onMessage);
    socket.addEventListener('close', this.onClose);
  }

  dispose(): void {
    this.socket.removeEventListener('message', this.onMessage);
    this.socket.removeEventListener('close', this.onClose);
  }

  list(path: string): Promise<Listing> {
    return this.simple({ op: 'listar', ruta: path }).then((r) => ({
      ruta: String(r['ruta'] ?? path),
      entradas: (r['entradas'] as RemoteEntry[] | undefined) ?? [],
      recortado: r['recortado'] === true,
    }));
  }

  createFolder(path: string): Promise<unknown> {
    return this.simple({ op: 'crear_carpeta', ruta: path });
  }

  rename(path: string, name: string): Promise<unknown> {
    return this.simple({ op: 'renombrar', ruta: path, nombre: name });
  }

  remove(path: string): Promise<unknown> {
    return this.simple({ op: 'eliminar', ruta: path });
  }

  /** Descarga en partes: cada parte se escribe y se confirma (control de flujo del agente). */
  download(
    path: string,
    sink: DownloadSink,
    onProgress: (done: number, total: number) => void,
  ): { done: Promise<void>; cancel: () => void } {
    const id = this.nextId++;
    let total = 0;
    let received = 0;
    let writing = Promise.resolve();
    const done = new Promise<void>((resolve, reject) => {
      const fail = (error: Error) => {
        this.waiters.delete(id);
        void writing
          .finally(() => sink.end(false))
          .then(
            () => reject(error),
            () => reject(error),
          );
      };
      this.waiters.set(id, {
        fail,
        onReply: (reply) => {
          if (reply['ok'] === false) {
            fail(new RemoteError(String(reply['error'] ?? 'No se pudo descargar.')));
            return true;
          }
          if (typeof reply['tamano'] === 'number') {
            total = reply['tamano'];
            onProgress(0, total);
          }
          if (reply['fin'] === true) {
            this.waiters.delete(id);
            writing = writing.then(() => sink.end(true));
            writing.then(
              () => resolve(),
              (error: unknown) => reject(error as Error),
            );
            return true;
          }
          return false;
        },
        onBinary: (data) => {
          writing = writing.then(async () => {
            await sink.write(data);
            received += data.length;
            onProgress(received, total);
            this.send({ op: 'ack', id });
          });
          writing.catch((error: unknown) => {
            this.send({ op: 'cancelar', id });
            fail(error instanceof Error ? error : new Error(String(error)));
          });
        },
      });
      this.send({ op: 'descargar', id, ruta: path });
    });
    return {
      done,
      cancel: () => {
        this.send({ op: 'cancelar', id });
        this.waiters.get(id)?.fail(new RemoteError('Descarga cancelada.'));
      },
    };
  }

  /** Sube en partes de 256 KB con hasta WINDOW sin confirmar; el agente lo deja con su nombre al final. */
  upload(
    path: string,
    source: UploadSource,
    replace: boolean,
    onProgress: (done: number, total: number) => void,
  ): { done: Promise<void>; cancel: () => void } {
    const id = this.nextId++;
    let cancelled = false;
    const done = new Promise<void>((resolve, reject) => {
      let started = false;
      let sent = 0;
      let inFlight = 0;
      let finishing = false;
      const fail = (error: Error) => {
        this.waiters.delete(id);
        reject(error);
      };
      const pump = async () => {
        while (!cancelled && inFlight < WINDOW && sent < source.size) {
          const length = Math.min(CHUNK_BYTES, source.size - sent);
          inFlight++;
          const offset = sent;
          sent += length;
          const data = await source.read(offset, length);
          this.sendBinary(id, data);
        }
        if (!cancelled && !finishing && sent >= source.size && inFlight === 0) {
          finishing = true;
          this.send({ op: 'subir_fin', id });
        }
      };
      this.waiters.set(id, {
        fail,
        onReply: (reply) => {
          if (reply['ok'] === false) {
            fail(new RemoteError(String(reply['error'] ?? 'No se pudo subir.')));
            return true;
          }
          if (reply['fin'] === true) {
            this.waiters.delete(id);
            onProgress(source.size, source.size);
            resolve();
            return true;
          }
          if (typeof reply['recibido'] === 'number') {
            inFlight--;
            onProgress(reply['recibido'], source.size);
          } else if (!started && reply['ok'] === true) {
            started = true;
          }
          pump().catch((error: unknown) => fail(error instanceof Error ? error : new Error(String(error))));
          return false;
        },
      });
      this.send({ op: 'subir', id, ruta: path, tamano: source.size, reemplazar: replace });
    });
    return {
      done,
      cancel: () => {
        cancelled = true;
        this.send({ op: 'cancelar', id });
        this.waiters.get(id)?.fail(new RemoteError('Subida cancelada.'));
      },
    };
  }

  private simple(request: Record<string, unknown>): Promise<Reply> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.waiters.set(id, {
        fail: reject,
        onReply: (reply) => {
          if (reply['ok'] === false) reject(new RemoteError(String(reply['error'] ?? 'No se pudo completar.')));
          else resolve(reply);
          return true;
        },
      });
      this.send({ ...request, id });
    });
  }

  private send(message: Record<string, unknown>): void {
    if (this.socket.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify(message));
  }

  private sendBinary(id: number, data: Uint8Array): void {
    const head = this.prefix === undefined ? 0 : 1;
    const out = new Uint8Array(head + 4 + data.length);
    if (head) out[0] = this.prefix!;
    new DataView(out.buffer).setUint32(head, id);
    out.set(data, head + 4);
    if (this.socket.readyState === WebSocket.OPEN) this.socket.send(out);
  }

  private readonly onMessage = (event: MessageEvent) => {
    if (event.data instanceof ArrayBuffer) {
      const head = this.prefix === undefined ? 0 : 1;
      if (event.data.byteLength < head + 4) return;
      if (head && new Uint8Array(event.data, 0, 1)[0] !== this.prefix) return; // no es una parte de archivo
      const id = new DataView(event.data).getUint32(head);
      this.waiters.get(id)?.onBinary?.(new Uint8Array(event.data, head + 4));
      return;
    }
    if (typeof event.data !== 'string') return;
    let reply: Reply;
    try {
      reply = JSON.parse(event.data) as Reply;
    } catch {
      return;
    }
    if (typeof reply.id !== 'number') return;
    const waiter = this.waiters.get(reply.id);
    if (waiter?.onReply(reply)) this.waiters.delete(reply.id);
  };

  private readonly onClose = () => {
    for (const waiter of [...this.waiters.values()]) waiter.fail(new RemoteError('Se cerró la sesión.'));
    this.waiters.clear();
  };
}

/** Partes de una ruta de Windows: "C:\\Users\\ana" → ["C:\\", "Users", "ana"]. */
export function pathSegments(path: string): string[] {
  if (!path) return [];
  const [drive, ...rest] = path.split('\\').filter((p, i) => p !== '' || i === 0);
  return [`${drive}\\`, ...rest.filter(Boolean)];
}

export function joinPath(folder: string, name: string): string {
  return folder.endsWith('\\') ? `${folder}${name}` : `${folder}\\${name}`;
}

/** Carpeta superior; la raíz de una unidad vuelve a la lista de unidades (""). */
export function parentPath(path: string): string {
  const segments = pathSegments(path);
  if (segments.length <= 1) return '';
  return segments.length === 2 ? segments[0]! : `${segments[0]}${segments.slice(1, -1).join('\\')}`;
}

/** Nombre que acepta el proceso principal para guardar (sin tildes ni caracteres raros). */
export function safeLocalName(name: string): string {
  const clean = name
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .replace(/[^\w .()-]/g, '_')
    .replace(/\.{2,}/g, '.')
    .slice(0, 100);
  return clean || 'archivo';
}

export function formatBytes(bytes: number | null): string {
  if (bytes === null) return '';
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

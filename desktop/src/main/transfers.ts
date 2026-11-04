import { open, rm, stat, type FileHandle } from 'node:fs/promises';
import { basename } from 'node:path';

/** Tamaño máximo de cada parte que pasa entre el renderer y el proceso principal (las partes del agente son de 256 KB). */
export const MAX_PART_BYTES = 256 * 1024;

interface Download {
  owner: number;
  handle: FileHandle;
  path: string;
}

interface Upload {
  owner: number;
  handle: FileHandle;
  size: number;
}

/**
 * Archivos de las sesiones remotas en el disco local (W-31): el renderer no toca rutas. Una descarga se escribe por
 * partes en el archivo que eligió el técnico con el diálogo del sistema; una subida se lee por partes del archivo que
 * eligió. Cada transferencia pertenece a la ventana que la abrió.
 */
export class Transfers {
  private nextId = 1;
  private readonly downloads = new Map<number, Download>();
  private readonly uploads = new Map<number, Upload>();

  async beginDownload(owner: number, path: string): Promise<number> {
    const handle = await open(path, 'w');
    const id = this.nextId++;
    this.downloads.set(id, { owner, handle, path });
    return id;
  }

  async write(owner: number, id: number, data: Uint8Array): Promise<void> {
    const download = this.downloads.get(id);
    if (!download || download.owner !== owner) throw new Error('Descarga desconocida.');
    if (data.byteLength > MAX_PART_BYTES) throw new Error('Parte demasiado grande.');
    await download.handle.write(data);
  }

  /** Cierra la descarga; si no terminó bien, borra el archivo a medias. */
  async endDownload(owner: number, id: number, ok: boolean): Promise<string | null> {
    const download = this.downloads.get(id);
    if (!download || download.owner !== owner) return null;
    this.downloads.delete(id);
    await download.handle.close();
    if (!ok) {
      await rm(download.path, { force: true });
      return null;
    }
    return download.path;
  }

  async beginUpload(owner: number, path: string): Promise<{ id: number; name: string; size: number }> {
    const info = await stat(path);
    if (!info.isFile()) throw new Error('Solo se pueden subir archivos.');
    const handle = await open(path, 'r');
    const id = this.nextId++;
    this.uploads.set(id, { owner, handle, size: info.size });
    return { id, name: basename(path), size: info.size };
  }

  async read(owner: number, id: number, offset: number, length: number): Promise<Uint8Array> {
    const upload = this.uploads.get(id);
    if (!upload || upload.owner !== owner) throw new Error('Subida desconocida.');
    if (
      !Number.isSafeInteger(offset) ||
      offset < 0 ||
      !Number.isInteger(length) ||
      length <= 0 ||
      length > MAX_PART_BYTES
    )
      throw new Error('Parte inválida.');
    const buffer = Buffer.alloc(Math.min(length, Math.max(0, upload.size - offset)));
    const { bytesRead } = await upload.handle.read(buffer, 0, buffer.length, offset);
    return buffer.subarray(0, bytesRead);
  }

  async endUpload(owner: number, id: number): Promise<void> {
    const upload = this.uploads.get(id);
    if (!upload || upload.owner !== owner) return;
    this.uploads.delete(id);
    await upload.handle.close();
  }

  /** La ventana se cerró: se sueltan sus archivos (las descargas a medias se borran). */
  async releaseOwner(owner: number): Promise<void> {
    for (const [id, d] of this.downloads) if (d.owner === owner) await this.endDownload(owner, id, false);
    for (const [id, u] of this.uploads) if (u.owner === owner) await this.endUpload(owner, id);
  }
}

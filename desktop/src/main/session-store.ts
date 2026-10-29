import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

/** Cifrado del sistema operativo (en Electron: safeStorage, que en Windows usa DPAPI del usuario). */
export interface Encryptor {
  isAvailable(): boolean;
  encrypt(plain: string): Buffer;
  decrypt(data: Buffer): string;
}

/** Los refresh tokens son 32 bytes en Base64 URL (43 caracteres); se acepta un margen. */
export function isValidRefreshToken(value: unknown): value is string {
  return typeof value === 'string' && /^[A-Za-z0-9_-]{20,200}$/.test(value);
}

/**
 * Guarda el refresh token del panel cifrado en disco, para no pedir contraseña cada vez que se abre.
 * Si el sistema no ofrece cifrado, no se guarda nada: la sesión dura lo que dure la ventana.
 */
export class SessionStore {
  constructor(
    private readonly file: string,
    private readonly encryptor: Encryptor,
  ) {}

  async load(): Promise<string | null> {
    if (!this.encryptor.isAvailable()) return null;
    try {
      const token = this.encryptor.decrypt(await readFile(this.file));
      return isValidRefreshToken(token) ? token : null;
    } catch {
      return null; // no existe, dañado o de otro usuario de Windows
    }
  }

  async save(token: string): Promise<void> {
    if (!isValidRefreshToken(token)) throw new Error('Token de sesión inválido.');
    if (!this.encryptor.isAvailable()) return;
    await mkdir(dirname(this.file), { recursive: true });
    await writeFile(this.file, this.encryptor.encrypt(token));
  }

  async clear(): Promise<void> {
    await rm(this.file, { force: true });
  }
}

/** Nombre de archivo propuesto por el renderer: sin rutas ni caracteres raros. */
export function isSafeFileName(value: unknown): value is string {
  return typeof value === 'string' && /^[\w .()-]{1,100}$/.test(value) && !value.includes('..');
}

/** Tope del archivo que el renderer puede pedir guardar (el instalador ronda los 100 MB). */
export const MAX_SAVE_BYTES = 300 * 1024 * 1024;

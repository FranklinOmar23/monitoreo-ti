import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { MAX_PART_BYTES, Transfers } from './transfers';

let dir: string;
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'monitoreoti-transfer-'));
});
afterEach(() => rmSync(dir, { recursive: true, force: true }));

describe('transferencias en el disco local', () => {
  it('escribe una descarga por partes y la borra si no terminó bien', async () => {
    const transfers = new Transfers();
    const ok = await transfers.beginDownload(1, join(dir, 'a.txt'));
    await transfers.write(1, ok, Buffer.from('hola '));
    await transfers.write(1, ok, Buffer.from('mundo'));
    expect(await transfers.endDownload(1, ok, true)).toBe(join(dir, 'a.txt'));
    expect(readFileSync(join(dir, 'a.txt'), 'utf8')).toBe('hola mundo');

    const broken = await transfers.beginDownload(1, join(dir, 'b.txt'));
    await transfers.write(1, broken, Buffer.from('a medias'));
    await transfers.endDownload(1, broken, false);
    expect(existsSync(join(dir, 'b.txt'))).toBe(false);
  });

  it('solo la ventana dueña usa la transferencia y las partes tienen tope', async () => {
    const transfers = new Transfers();
    const id = await transfers.beginDownload(1, join(dir, 'c.txt'));
    await expect(transfers.write(2, id, Buffer.from('x'))).rejects.toThrow('Descarga desconocida');
    await expect(transfers.write(1, id, new Uint8Array(MAX_PART_BYTES + 1))).rejects.toThrow('demasiado grande');
    await transfers.releaseOwner(1);
  });

  it('lee una subida por partes', async () => {
    writeFileSync(join(dir, 'origen.txt'), 'abcdef');
    const transfers = new Transfers();
    const upload = await transfers.beginUpload(7, join(dir, 'origen.txt'));
    expect(upload).toMatchObject({ name: 'origen.txt', size: 6 });
    expect(Buffer.from(await transfers.read(7, upload.id, 2, 3)).toString()).toBe('cde');
    expect(Buffer.from(await transfers.read(7, upload.id, 4, 100)).toString()).toBe('ef');
    await transfers.endUpload(7, upload.id);
  });
});

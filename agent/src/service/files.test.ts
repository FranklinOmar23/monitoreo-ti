import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { CHUNK_BYTES, filesHandlerFactory, frame, type FileActivity } from './files';
import type { SessionHandler } from './remote-sessions';

let dir: string;
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'monitoreoti-archivos-'));
});
afterEach(() => rmSync(dir, { recursive: true, force: true }));

function setup() {
  const texts: Record<string, unknown>[] = [];
  const binaries: Uint8Array[] = [];
  const activity: { sessionId: number; activity: FileActivity }[] = [];
  const handler: SessionHandler = filesHandlerFactory((sessionId, a) => activity.push({ sessionId, activity: a }))({
    sessionId: 12,
    technician: 'Ana',
    askConsent: false,
    viewOnly: false,
    allowFiles: false,
    sendText: (t) => texts.push(JSON.parse(t) as Record<string, unknown>),
    sendBinary: (b) => binaries.push(b),
    close: vi.fn(),
  });
  const send = (request: Record<string, unknown>) => handler.onText(JSON.stringify(request));
  const replyFor = (id: number, key = 'ok') =>
    vi.waitFor(() => {
      const found = texts.find((t) => t['id'] === id && key in t);
      if (!found) throw new Error(`Sin respuesta ${key} para ${id}`);
      return found;
    });
  return { handler, texts, binaries, activity, send, replyFor };
}

describe('explorador de archivos en el agente', () => {
  it('lista una carpeta con tamaños y distingue carpetas', async () => {
    writeFileSync(join(dir, 'informe.txt'), 'hola');
    mkdirSync(join(dir, 'fotos'));
    const { send, replyFor } = setup();

    send({ op: 'listar', id: 1, ruta: dir });
    const reply = await replyFor(1);
    expect(reply['ok']).toBe(true);
    expect(reply['entradas']).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ nombre: 'informe.txt', tipo: 'archivo', tamano: 4 }),
        expect.objectContaining({ nombre: 'fotos', tipo: 'carpeta', tamano: null }),
      ]),
    );
  });

  it('descarga en partes con su id, espera las confirmaciones y lo informa para la auditoría', async () => {
    const content = Buffer.alloc(CHUNK_BYTES * 5 + 10, 7);
    writeFileSync(join(dir, 'grande.bin'), content);
    const { send, replyFor, binaries, texts, activity } = setup();

    send({ op: 'descargar', id: 2, ruta: join(dir, 'grande.bin') });
    expect((await replyFor(2))['tamano']).toBe(content.length);
    // Con 4 partes sin confirmar se detiene.
    await vi.waitFor(() => expect(binaries).toHaveLength(4));
    await new Promise((r) => setTimeout(r, 50));
    expect(binaries).toHaveLength(4);

    for (let i = 0; i < 6; i++) send({ op: 'ack', id: 2 });
    await replyFor(2, 'fin');
    const data = Buffer.concat(binaries.map((b) => b.subarray(4)));
    expect(data.equals(content)).toBe(true);
    expect(new DataView(binaries[0]!.buffer, binaries[0]!.byteOffset).getUint32(0)).toBe(2);
    expect(texts.at(-1)).toEqual({ id: 2, fin: true });
    expect(activity).toEqual([
      { sessionId: 12, activity: { accion: 'descargar', ruta: join(dir, 'grande.bin'), tamano: content.length } },
    ]);
  });

  it('sube a un archivo temporal y solo lo deja con su nombre al recibirlo completo', async () => {
    const target = join(dir, 'nuevo.txt');
    const { handler, send, replyFor, texts } = setup();

    send({ op: 'subir', id: 3, ruta: target, tamano: 10 });
    await replyFor(3);
    handler.onBinary(frame(3, Buffer.from('hola ')));
    handler.onBinary(frame(3, Buffer.from('mundo')));
    await vi.waitFor(() => expect(texts.some((t) => t['recibido'] === 10)).toBe(true));
    expect(existsSync(target)).toBe(false);

    send({ op: 'subir_fin', id: 3 });
    await replyFor(3, 'fin');
    expect(readFileSync(target, 'utf8')).toBe('hola mundo');
    expect(existsSync(`${target}.monitoreoti-parte`)).toBe(false);
  });

  it('no pisa un archivo existente salvo que se pida reemplazarlo', async () => {
    writeFileSync(join(dir, 'existe.txt'), 'viejo');
    const { send, replyFor } = setup();

    send({ op: 'subir', id: 4, ruta: join(dir, 'existe.txt'), tamano: 1 });
    expect(await replyFor(4)).toMatchObject({ ok: false, error: 'Ya existe un archivo con ese nombre.' });
  });

  it('crea, renombra y elimina, y rechaza rutas relativas o nombres con barras', async () => {
    const { send, replyFor, activity } = setup();

    send({ op: 'crear_carpeta', id: 5, ruta: join(dir, 'a') });
    await replyFor(5);
    send({ op: 'renombrar', id: 6, ruta: join(dir, 'a'), nombre: 'b' });
    await replyFor(6);
    send({ op: 'renombrar', id: 7, ruta: join(dir, 'b'), nombre: '..\fuera' });
    expect((await replyFor(7))['ok']).toBe(false);
    send({ op: 'eliminar', id: 8, ruta: join(dir, 'b') });
    await replyFor(8);
    send({ op: 'listar', id: 9, ruta: 'relativa/carpeta' });
    expect((await replyFor(9))['ok']).toBe(false);

    expect(existsSync(join(dir, 'b'))).toBe(false);
    expect(activity.map((a) => a.activity.accion)).toEqual(['crear_carpeta', 'renombrar', 'eliminar']);
  });
});

import { describe, expect, it, vi } from 'vitest';
import { CHUNK_BYTES, FileClient, joinPath, parentPath, pathSegments, safeLocalName } from './fileClient';

class FakeSocket extends EventTarget {
  readyState = 1;
  sent: (string | Uint8Array)[] = [];
  send(data: string | Uint8Array) {
    this.sent.push(data);
  }
  reply(data: Record<string, unknown>) {
    this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(data) }));
  }
  binary(id: number, bytes: number[]) {
    const out = new Uint8Array(4 + bytes.length);
    new DataView(out.buffer).setUint32(0, id);
    out.set(bytes, 4);
    this.dispatchEvent(new MessageEvent('message', { data: out.buffer }));
  }
  texts() {
    return this.sent
      .filter((s): s is string => typeof s === 'string')
      .map((s) => JSON.parse(s) as Record<string, unknown>);
  }
}

function setup() {
  const socket = new FakeSocket();
  return { socket, client: new FileClient(socket as unknown as WebSocket) };
}

describe('rutas de Windows', () => {
  it('parte, une y sube de nivel', () => {
    expect(pathSegments('C:\\Users\\ana')).toEqual(['C:\\', 'Users', 'ana']);
    expect(pathSegments('C:\\')).toEqual(['C:\\']);
    expect(joinPath('C:\\', 'Users')).toBe('C:\\Users');
    expect(joinPath('C:\\Users', 'ana')).toBe('C:\\Users\\ana');
    expect(parentPath('C:\\Users\\ana')).toBe('C:\\Users');
    expect(parentPath('C:\\Users')).toBe('C:\\');
    expect(parentPath('C:\\')).toBe('');
  });

  it('deja un nombre local que el proceso principal acepta', () => {
    expect(safeLocalName('Informe año 2026: final?.xlsx')).toBe('Informe ano 2026_ final_.xlsx');
    expect(safeLocalName('..\\..\\x')).not.toContain('..');
  });
});

describe('cliente de archivos', () => {
  it('lista y devuelve el error del agente en palabras', async () => {
    const { socket, client } = setup();
    const listing = client.list('C:\\');
    expect(socket.texts()[0]).toEqual({ op: 'listar', ruta: 'C:\\', id: 1 });
    socket.reply({
      id: 1,
      ok: true,
      ruta: 'C:\\',
      entradas: [{ nombre: 'Users', tipo: 'carpeta', tamano: null, modificado: null }],
    });
    expect((await listing).entradas).toHaveLength(1);

    const failing = client.remove('C:\\Windows');
    socket.reply({ id: 2, ok: false, error: 'Sin permiso (o el archivo está en uso).' });
    await expect(failing).rejects.toThrow('Sin permiso');
  });

  it('descarga: escribe cada parte, la confirma y cierra el archivo al final', async () => {
    const { socket, client } = setup();
    const written: number[][] = [];
    const end = vi.fn(() => Promise.resolve());
    const progress = vi.fn();
    const job = client.download(
      'C:\\a.txt',
      {
        write: (d) => {
          written.push([...d]);
          return Promise.resolve();
        },
        end,
      },
      progress,
    );

    socket.reply({ id: 1, ok: true, tamano: 5 });
    socket.binary(1, [1, 2, 3]);
    socket.binary(1, [4, 5]);
    socket.reply({ id: 1, fin: true });
    await job.done;

    expect(written).toEqual([
      [1, 2, 3],
      [4, 5],
    ]);
    expect(socket.texts().filter((t) => t['op'] === 'ack')).toHaveLength(2);
    expect(end).toHaveBeenCalledWith(true);
    expect(progress).toHaveBeenLastCalledWith(5, 5);
  });

  it('descarga fallida: borra lo escrito a medias', async () => {
    const { socket, client } = setup();
    const end = vi.fn(() => Promise.resolve());
    const job = client.download('C:\\a.txt', { write: () => Promise.resolve(), end }, vi.fn());
    socket.reply({ id: 1, ok: false, error: 'No existe.' });

    await expect(job.done).rejects.toThrow('No existe.');
    expect(end).toHaveBeenCalledWith(false);
  });

  it('subida: manda partes con su id, como mucho 4 sin confirmar, y cierra con subir_fin', async () => {
    const { socket, client } = setup();
    const size = CHUNK_BYTES * 5 + 3;
    const job = client.upload(
      'C:\\x.bin',
      { size, read: (_o, length) => Promise.resolve(new Uint8Array(length)) },
      false,
      vi.fn(),
    );

    expect(socket.texts()[0]).toMatchObject({ op: 'subir', ruta: 'C:\\x.bin', tamano: size, reemplazar: false });
    socket.reply({ id: 1, ok: true });
    await vi.waitFor(() => expect(socket.sent.filter((s) => typeof s !== 'string')).toHaveLength(4));

    for (let n = 1; n <= 6; n++) {
      socket.reply({ id: 1, recibido: Math.min(size, n * CHUNK_BYTES) });
      await new Promise((r) => setTimeout(r, 0));
    }
    await vi.waitFor(() => expect(socket.texts().some((t) => t['op'] === 'subir_fin')).toBe(true));
    socket.reply({ id: 1, ok: true, fin: true });
    await job.done;

    const parts = socket.sent.filter((s): s is Uint8Array => typeof s !== 'string');
    expect(parts).toHaveLength(6);
    expect(new DataView(parts[0]!.buffer).getUint32(0)).toBe(1);
    expect(parts.reduce((sum, p) => sum + p.length - 4, 0)).toBe(size);
  });
});

import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createEnvelope, type Envelope } from '@monitoreoti/shared';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { Outbox } from './outbox';
import { Sender } from './sender';

const message = (tipo: string, id: string): Envelope => createEnvelope(tipo, { n: id }, { id });

describe('Outbox', () => {
  let dir: string;
  beforeEach(async () => {
    dir = await mkdtemp(join(tmpdir(), 'outbox-'));
  });
  afterEach(() => rm(dir, { recursive: true, force: true }));

  it('devuelve los mensajes en el orden en que se encolaron y los borra al confirmar', () => {
    const outbox = new Outbox(':memory:');
    outbox.enqueue(message('metricas', 'a'));
    outbox.enqueue(message('heartbeat', 'b'));
    outbox.enqueue(message('metricas', 'c'));

    expect(outbox.peek(10).map((m) => m.id)).toEqual(['a', 'b', 'c']);
    expect(outbox.peek(2)).toHaveLength(2);

    outbox.remove('a');
    expect(outbox.peek(10).map((m) => m.id)).toEqual(['b', 'c']);
    expect(outbox.count()).toBe(2);
  });

  it('no duplica un mensaje encolado dos veces', () => {
    const outbox = new Outbox(':memory:');
    outbox.enqueue(message('metricas', 'a'));
    outbox.enqueue(message('metricas', 'a'));

    expect(outbox.count()).toBe(1);
  });

  it('sobrevive a un reinicio: lo no confirmado sigue en disco', () => {
    const path = join(dir, 'datos', 'outbox.db');
    const first = new Outbox(path);
    first.enqueue(message('inventario_software', 'x'));
    first.close();

    const second = new Outbox(path);

    expect(second.peek(10).map((m) => m.id)).toEqual(['x']);
    second.close();
  });

  it('al llenarse descarta primero las métricas más viejas y conserva los inventarios', () => {
    const outbox = new Outbox(':memory:', 3);
    outbox.enqueue(message('inventario_hardware', 'hw'));
    outbox.enqueue(message('metricas', 'm1'));
    outbox.enqueue(message('metricas', 'm2'));
    outbox.enqueue(message('metricas', 'm3'));

    expect(outbox.peek(10).map((m) => m.id)).toEqual(['hw', 'm2', 'm3']);
  });
});

describe('Sender', () => {
  it('envía en orden y borra cada mensaje solo tras su ack', async () => {
    const outbox = new Outbox(':memory:');
    const sent: string[] = [];
    const sender = new Sender(outbox, {
      request: (envelope) => {
        sent.push(envelope.id);
        return Promise.resolve();
      },
    });
    ['a', 'b', 'c'].forEach((id) => outbox.enqueue(message('metricas', id)));

    await sender.drain();

    expect(sent).toEqual(['a', 'b', 'c']);
    expect(outbox.count()).toBe(0);
  });

  it('si un envío falla se detiene sin perder ni saltarse mensajes y reintenta después', async () => {
    const outbox = new Outbox(':memory:');
    let online = false;
    const sent: string[] = [];
    const sender = new Sender(outbox, {
      request: (envelope) => {
        if (!online) return Promise.reject(new Error('sin conexión'));
        sent.push(envelope.id);
        return Promise.resolve();
      },
    });
    ['a', 'b'].forEach((id) => outbox.enqueue(message('metricas', id)));

    await sender.drain();
    expect(outbox.count()).toBe(2);
    expect(sent).toEqual([]);

    online = true;
    await sender.drain();
    expect(sent).toEqual(['a', 'b']);
    expect(outbox.count()).toBe(0);
  });

  it('un mensaje sin ack se reenvía y no se pierde', async () => {
    const outbox = new Outbox(':memory:');
    let attempts = 0;
    const sender = new Sender(outbox, {
      request: () => (++attempts === 1 ? Promise.reject(new Error('sin ack')) : Promise.resolve()),
    });
    outbox.enqueue(message('metricas', 'a'));

    await sender.drain();
    expect(outbox.count()).toBe(1);
    await sender.drain();

    expect(outbox.count()).toBe(0);
    expect(attempts).toBe(2);
  });
});

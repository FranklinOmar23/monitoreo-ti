import { describe, expect, it } from 'vitest';
import { PROTOCOL_VERSION, createEnvelope, isEnvelope } from './protocol';

describe('sobre del protocolo', () => {
  it('crea el sobre con versión, id y fecha UTC', () => {
    const now = new Date('2026-10-26T19:40:00Z');
    const envelope = createEnvelope('heartbeat', {}, { id: 'abc', now });

    expect(envelope).toEqual({
      v: PROTOCOL_VERSION,
      tipo: 'heartbeat',
      id: 'abc',
      ts: '2026-10-26T19:40:00.000Z',
      datos: {},
    });
  });

  it('genera un id único si no se indica', () => {
    const a = createEnvelope('metricas', { cpu: 10 });
    const b = createEnvelope('metricas', { cpu: 10 });

    expect(a.id).not.toBe(b.id);
    expect(a.id).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('reconoce mensajes válidos y rechaza los incompletos', () => {
    expect(isEnvelope(JSON.parse(JSON.stringify(createEnvelope('hola', { version: '0.1.0' }))))).toBe(true);
    expect(isEnvelope({ v: 1, tipo: 'hola', id: 'x' })).toBe(false);
    expect(isEnvelope(null)).toBe(false);
    expect(isEnvelope('hola')).toBe(false);
  });
});

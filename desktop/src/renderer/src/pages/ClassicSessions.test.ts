import { describe, expect, it } from 'vitest';
import { classicWithoutSessions } from './FichaPage';

describe('agente clásico y sesiones remotas (decisión 84)', () => {
  it('solo el clásico anterior a la 0.2.0 queda sin sesiones', () => {
    expect(classicWithoutSessions('0.1.0-clasico')).toBe(true);
    expect(classicWithoutSessions('0.2.0-clasico')).toBe(false);
    expect(classicWithoutSessions('0.10.0-clasico')).toBe(false);
    expect(classicWithoutSessions('1.0.0-clasico')).toBe(false);
    expect(classicWithoutSessions('0.4.0')).toBe(false);
    expect(classicWithoutSessions(null)).toBe(false);
  });
});

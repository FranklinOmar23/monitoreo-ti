import { describe, expect, it } from 'vitest';
import { MACHINE_STATUSES, PORT_COLORS, STATUS_COLORS, portColor, worstStatus } from './status';

describe('estados del semáforo', () => {
  it('cada estado de máquina tiene color', () => {
    for (const status of MACHINE_STATUSES) {
      expect(STATUS_COLORS[status]).toMatch(/^#[0-9a-f]{6}$/);
    }
  });

  it('elige el peor estado de un grupo', () => {
    expect(worstStatus(['sano', 'advertencia', 'desconectado'])).toBe('advertencia');
    expect(worstStatus(['sano', 'critico', 'advertencia'])).toBe('critico');
    expect(worstStatus(['baja', 'desconectado'])).toBe('desconectado');
    expect(worstStatus([])).toBeUndefined();
  });

  it('un puerto troncal conectado se pinta azul', () => {
    expect(portColor('conectado', true)).toBe(PORT_COLORS.troncal);
    expect(portColor('conectado', false)).toBe(PORT_COLORS.conectado);
    expect(portColor('caido', true)).toBe(PORT_COLORS.caido);
  });
});

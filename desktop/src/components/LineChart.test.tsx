import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { count } from '../renderer/src/data/format';
import { LineChart } from './LineChart';

const MIN = 60_000;

describe('LineChart (decisión 90)', () => {
  it('corta la línea donde faltan muestras y marca la franja "sin datos" en vez de caer a 0 %', () => {
    const t0 = 0;
    const times = [t0, t0 + 5 * MIN, t0 + 10 * MIN, t0 + 120 * MIN, t0 + 125 * MIN];
    const { container } = render(
      <LineChart
        xLabels={[]}
        times={times}
        domain={[t0, t0 + 125 * MIN]}
        maxGapMs={15 * MIN}
        series={[{ name: 'CPU', color: 'cpu', values: [40, 50, 45, 100, 100] }]}
      />,
    );
    expect(container.querySelectorAll('.chart-line')).toHaveLength(2);
    expect(container.querySelectorAll('.chart-gap')).toHaveLength(1);
    expect(container.textContent).toContain('SIN DATOS');
    // Ningún tramo baja a 0 %: el punto más bajo es el 40 %.
    const d = [...container.querySelectorAll('.chart-line')].map((p) => p.getAttribute('d')).join(' ');
    expect(d).not.toMatch(/ 224\.0/);
  });

  it('un valor nulo corta la línea sin dibujarlo como cero', () => {
    const { container } = render(
      <LineChart xLabels={[]} series={[{ name: 'RAM', color: 'ram', values: [30, 31, null, 33, 34] }]} />,
    );
    expect(container.querySelectorAll('.chart-line')).toHaveLength(2);
  });

  it('marca el comienzo del rango sin datos cuando el equipo empezó a reportar tarde', () => {
    const { container } = render(
      <LineChart
        xLabels={[]}
        times={[600 * MIN, 605 * MIN]}
        domain={[0, 605 * MIN]}
        maxGapMs={15 * MIN}
        series={[{ name: 'CPU', color: 'cpu', values: [10, 12] }]}
      />,
    );
    expect(container.querySelectorAll('.chart-gap')).toHaveLength(1);
  });
});

describe('count', () => {
  it('pone la palabra en singular o plural', () => {
    expect(count(1, 'activo')).toBe('1 activo');
    expect(count(3, 'activo')).toBe('3 activos');
    expect(count(0, 'equipo')).toBe('0 equipos');
    expect(count(2, 'sucursal')).toBe('2 sucursales');
    expect(count(1, 'vez', 'veces')).toBe('1 vez');
    expect(count(1200, 'equipo')).toBe('1,200 equipos');
  });
});

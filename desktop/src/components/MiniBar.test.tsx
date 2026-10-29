import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MiniBar, metricLevel } from './MiniBar';

describe('MiniBar', () => {
  it('clasifica el uso: desde 80% advertencia y desde 90% crítico', () => {
    expect(metricLevel(79)).toBe('sano');
    expect(metricLevel(83)).toBe('advertencia');
    expect(metricLevel(90)).toBe('critico');
  });

  it('muestra un guion accesible cuando el equipo no reporta la métrica', () => {
    render(<MiniBar label="CPU" percent={null} />);

    expect(screen.getByLabelText('CPU: sin datos')).toBeTruthy();
  });
});

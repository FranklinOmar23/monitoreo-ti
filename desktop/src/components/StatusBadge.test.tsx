import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { StatusBadge } from './StatusBadge';

describe('StatusBadge', () => {
  it('muestra el texto del estado, no solo el color', () => {
    render(<StatusBadge status="critico" />);

    const badge = screen.getByText('Crítico');
    expect(badge.getAttribute('data-status')).toBe('critico');
  });
});

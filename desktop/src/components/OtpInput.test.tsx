import { useState } from 'react';
import { act, cleanup, fireEvent, render, renderHook, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { OtpInput } from './OtpInput';
import { useLeaving } from './useLeaving';

afterEach(cleanup);

function Harness({ onComplete }: { onComplete: (code: string) => void }) {
  const [value, setValue] = useState('');
  return <OtpInput value={value} onChange={setValue} onComplete={onComplete} autoFocus />;
}

const box = (n: number) => screen.getByLabelText(`Dígito ${n} de 6`) as HTMLInputElement;

describe('código en seis casillas (W-02)', () => {
  it('avanza al escribir, Retroceso vuelve a la anterior y al completar se envía solo', () => {
    const onComplete = vi.fn();
    render(<Harness onComplete={onComplete} />);
    expect(document.activeElement).toBe(box(1));

    fireEvent.change(box(1), { target: { value: '4' } });
    expect(document.activeElement).toBe(box(2));
    fireEvent.change(box(2), { target: { value: 'x' } });
    expect(box(2).value).toBe('');

    fireEvent.keyDown(box(2), { key: 'Backspace' });
    expect(box(1).value).toBe('');
    expect(document.activeElement).toBe(box(1));

    for (const [i, digit] of ['1', '2', '3', '4', '5', '6'].entries())
      fireEvent.change(box(i + 1), { target: { value: digit } });
    expect(onComplete).toHaveBeenCalledWith('123456');
  });

  it('pegar el código llena todas las casillas', () => {
    const onComplete = vi.fn();
    render(<Harness onComplete={onComplete} />);
    fireEvent.paste(box(3), { clipboardData: { getData: () => ' 987 654 ' } });
    expect([1, 2, 3, 4, 5, 6].map((n) => box(n).value).join('')).toBe('987654');
    expect(onComplete).toHaveBeenCalledWith('987654');
  });

  it('las flechas mueven el cursor', () => {
    render(<Harness onComplete={vi.fn()} />);
    fireEvent.keyDown(box(1), { key: 'ArrowRight' });
    expect(document.activeElement).toBe(box(2));
    fireEvent.keyDown(box(2), { key: 'ArrowLeft' });
    expect(document.activeElement).toBe(box(1));
  });
});

describe('useLeaving', () => {
  it('sin animaciones quita al momento lo que salió de la lista', () => {
    const { result, rerender } = renderHook(({ items }) => useLeaving(items, (n) => n), {
      initialProps: { items: [1, 2, 3] },
    });
    rerender({ items: [1, 3] });
    expect(result.current).toEqual([
      { item: 1, leaving: false },
      { item: 3, leaving: false },
    ]);
  });

  it('con animaciones deja lo que salió en su lugar mientras se desvanece', () => {
    vi.useFakeTimers();
    vi.stubGlobal('matchMedia', () => ({ matches: false }));
    try {
      const { result, rerender } = renderHook(({ items }) => useLeaving(items, (n) => n), {
        initialProps: { items: [1, 2, 3] },
      });
      rerender({ items: [1, 3, 4] });
      expect(result.current).toEqual([
        { item: 1, leaving: false },
        { item: 2, leaving: true },
        { item: 3, leaving: false },
        { item: 4, leaving: false },
      ]);
      act(() => vi.advanceTimersByTime(400));
      expect(result.current.map((e) => e.item)).toEqual([1, 3, 4]);
    } finally {
      vi.unstubAllGlobals();
      vi.useRealTimers();
    }
  });
});

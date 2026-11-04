import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { desktopLabel, parseScreenUpdate, wheelDelta } from './DesktopView';
import { DesktopWindow } from './DesktopWindow';

afterEach(cleanup);

/** Arma un mensaje de pantalla como lo manda el asistente (ScreenCapture.Capture). */
function update(
  width: number,
  height: number,
  rects: { x: number; y: number; w: number; h: number; jpeg: number[] }[],
) {
  const size = 7 + rects.reduce((sum, r) => sum + 12 + r.jpeg.length, 0);
  const buffer = new ArrayBuffer(size);
  const view = new DataView(buffer);
  view.setUint8(0, 1);
  view.setUint16(1, width, true);
  view.setUint16(3, height, true);
  view.setUint16(5, rects.length, true);
  let offset = 7;
  for (const r of rects) {
    view.setUint16(offset, r.x, true);
    view.setUint16(offset + 2, r.y, true);
    view.setUint16(offset + 4, r.w, true);
    view.setUint16(offset + 6, r.h, true);
    view.setInt32(offset + 8, r.jpeg.length, true);
    new Uint8Array(buffer, offset + 12).set(r.jpeg);
    offset += 12 + r.jpeg.length;
  }
  return buffer;
}

describe('visor del escritorio remoto (W-28)', () => {
  it('lee el tamaño de la pantalla y cada rectángulo con su JPEG', () => {
    const parsed = parseScreenUpdate(
      update(1920, 1080, [
        { x: 0, y: 0, w: 1920, h: 64, jpeg: [0xff, 0xd8, 1] },
        { x: 1344, y: 576, w: 128, h: 64, jpeg: [0xff, 0xd8, 2, 3] },
      ]),
    )!;

    expect(parsed.width).toBe(1920);
    expect(parsed.height).toBe(1080);
    expect(parsed.rects.map(({ x, y, w, h }) => ({ x, y, w, h }))).toEqual([
      { x: 0, y: 0, w: 1920, h: 64 },
      { x: 1344, y: 576, w: 128, h: 64 },
    ]);
    expect([...parsed.rects[1]!.jpeg]).toEqual([0xff, 0xd8, 2, 3]);
  });

  it('descarta lo que no es una actualización de pantalla', () => {
    expect(parseScreenUpdate(new ArrayBuffer(3))).toBeNull();
    const other = update(10, 10, []);
    new DataView(other).setUint8(0, 9);
    expect(parseScreenUpdate(other)).toBeNull();
  });

  it('convierte la rueda del navegador a pasos de Windows', () => {
    expect(wheelDelta(100)).toBe(-120); // hacia el usuario: bajar
    expect(wheelDelta(-3)).toBe(120); // hacia adelante: subir
    expect(wheelDelta(0)).toBe(0);
  });

  it('explica en qué escritorio de Windows está el equipo', () => {
    expect(desktopLabel('Default')).toBeNull();
    expect(desktopLabel('Winlogon')).toContain('pantalla de bloqueo');
    expect(desktopLabel('Secure')).toContain('UAC');
  });

  it('sin ticket explica que hay que abrirla otra vez desde la ficha', async () => {
    render(<DesktopWindow loadTicket={() => Promise.resolve(null)} createSocket={vi.fn()} />);
    expect(await screen.findByText(/Ábrala de nuevo desde la ficha/)).toBeTruthy();
  });
});

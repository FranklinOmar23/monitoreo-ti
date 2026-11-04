/**
 * Ícono de la bandeja dibujado aquí (sin archivos de imagen): el logo de Centinela —un disco verde azulado con dos
 * anillos y un punto— y, si hay alertas críticas, un punto rojo abajo a la derecha. Devuelve píxeles BGRA para
 * nativeImage.createFromBitmap.
 */

type Rgb = readonly [number, number, number];

const TEAL: Rgb = [15, 118, 110];
const WHITE: Rgb = [255, 255, 255];
const RED: Rgb = [220, 38, 38];

/** Qué color (y cuánto cubre) tiene el punto (x, y) en un ícono de 1×1. */
function paint(x: number, y: number, badge: boolean): { color: Rgb; alpha: number } | null {
  if (badge) {
    const bx = x - 0.76;
    const by = y - 0.76;
    const d = Math.hypot(bx, by);
    if (d <= 0.2) return { color: RED, alpha: 1 };
    if (d <= 0.26) return { color: WHITE, alpha: 1 };
  }
  const d = Math.hypot(x - 0.5, y - 0.5);
  if (d > 0.48) return null;
  // Como el ícono "logo" del panel: anillos de radio 9 y 5 y un punto de 1 sobre 24.
  const ring = (radius: number) => Math.abs(d - radius) <= 0.032;
  if (ring(0.3) || ring(0.17) || d <= 0.06) return { color: WHITE, alpha: 1 };
  return { color: TEAL, alpha: 1 };
}

export function trayIconBitmap(size: number, badge: boolean): Buffer {
  const samples = 4;
  const pixels = Buffer.alloc(size * size * 4);
  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let r = 0;
      let g = 0;
      let b = 0;
      let a = 0;
      for (let sy = 0; sy < samples; sy++) {
        for (let sx = 0; sx < samples; sx++) {
          const hit = paint((px + (sx + 0.5) / samples) / size, (py + (sy + 0.5) / samples) / size, badge);
          if (!hit) continue;
          r += hit.color[0] * hit.alpha;
          g += hit.color[1] * hit.alpha;
          b += hit.color[2] * hit.alpha;
          a += hit.alpha;
        }
      }
      const offset = (py * size + px) * 4;
      const coverage = a / (samples * samples);
      // Premultiplicado, como lo espera Windows.
      pixels[offset] = Math.round(b / (samples * samples));
      pixels[offset + 1] = Math.round(g / (samples * samples));
      pixels[offset + 2] = Math.round(r / (samples * samples));
      pixels[offset + 3] = Math.round(coverage * 255);
    }
  }
  return pixels;
}

/** Texto del tooltip y del menú según las alertas críticas abiertas. */
export function criticalLabel(count: number): string {
  if (count <= 0) return 'Sin alertas críticas';
  return count === 1 ? '1 alerta crítica' : `${count} alertas críticas`;
}

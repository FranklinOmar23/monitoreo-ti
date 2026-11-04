/**
 * Color de la empresa (W-40): reemplaza al principal del panel (botones, enlaces, pestañas, selección) en tema claro y
 * oscuro. Los colores de estado (sano, advertencia, crítico) no se tocan. Se ajusta el tono para que se lea siempre:
 * el principal contra la superficie (como texto de enlace) y el texto encima del botón.
 */

export interface BrandTones {
  primary: string;
  hover: string;
  onPrimary: string;
}

/** Superficies del panel (styles.css): contra ellas se mide el principal. */
const LIGHT_SURFACE = '#ffffff';
const DARK_SURFACE = '#16262c';
/** WCAG AA para texto normal: el principal se usa como color de enlaces. */
const MIN_CONTRAST = 4.5;

export function isHexColor(value: string): boolean {
  return /^#[0-9a-f]{6}$/i.test(value);
}

function toRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.slice(1), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function toHex([r, g, b]: [number, number, number]): string {
  return `#${[r, g, b]
    .map((c) =>
      Math.round(Math.min(255, Math.max(0, c)))
        .toString(16)
        .padStart(2, '0'),
    )
    .join('')}`;
}

function luminance(hex: string): number {
  const [r, g, b] = toRgb(hex).map((c) => {
    const s = c / 255;
    return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
  }) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (hi + 0.05) / (lo + 0.05);
}

/** Mezcla con blanco (amount > 0) o negro (amount < 0): aclarar u oscurecer manteniendo el tono. */
function shade(hex: string, amount: number): string {
  const target = amount > 0 ? 255 : 0;
  const t = Math.abs(amount);
  return toHex(toRgb(hex).map((c) => c + (target - c) * t) as [number, number, number]);
}

/** Lo más cerca del color original que se lea contra la superficie. */
function readableOn(hex: string, surface: string, direction: 1 | -1): string {
  let color = hex;
  for (let step = 0; step < 20 && contrast(color, surface) < MIN_CONTRAST; step++)
    color = shade(color, 0.08 * direction);
  return color;
}

function onColor(primary: string): string {
  return contrast(primary, '#ffffff') >= contrast(primary, '#0d1b20') ? '#ffffff' : '#0d1b20';
}

/** Tonos del tema claro y oscuro a partir del color de la empresa. */
export function brandTones(hex: string): { light: BrandTones; dark: BrandTones } {
  const base = hex.toLowerCase();
  const light = readableOn(base, LIGHT_SURFACE, -1);
  const dark = readableOn(base, DARK_SURFACE, 1);
  return {
    light: { primary: light, hover: shade(light, -0.15), onPrimary: onColor(light) },
    dark: { primary: dark, hover: shade(dark, 0.15), onPrimary: onColor(dark) },
  };
}

const STYLE_ID = 'marca-empresa';

function rules(t: BrandTones): string {
  return `--color-primary: ${t.primary}; --color-primary-hover: ${t.hover}; --color-on-primary: ${t.onPrimary}; --chart-cpu: ${t.primary};`;
}

/** Pinta el panel con el color de la empresa; null vuelve a los colores de Centinela. */
export function applyBrandColor(color: string | null, doc: Document = document): void {
  doc.getElementById(STYLE_ID)?.remove();
  if (!color || !isHexColor(color)) return;
  const { light, dark } = brandTones(color);
  const style = doc.createElement('style');
  style.id = STYLE_ID;
  // Mismos selectores que styles.css; al ir después, ganan.
  style.textContent = `:root { ${rules(light)} }\n@media (prefers-color-scheme: dark) { :root { ${rules(dark)} } }`;
  doc.head.appendChild(style);
}

const CACHE_KEY = 'centinela.apariencia';

/** La última apariencia vista, para la pantalla de carga (antes de saber la sesión). */
export function cachedAppearance(): { color: string | null; logo: string | null } | null {
  try {
    const raw = localStorage.getItem(CACHE_KEY);
    if (!raw) return null;
    const value = JSON.parse(raw) as { color?: unknown; logo?: unknown };
    return {
      color: typeof value.color === 'string' && isHexColor(value.color) ? value.color : null,
      logo: typeof value.logo === 'string' && value.logo.startsWith('data:image/') ? value.logo : null,
    };
  } catch {
    return null;
  }
}

export function cacheAppearance(value: { color: string | null; logo: string | null } | null): void {
  try {
    if (value && (value.color || value.logo)) localStorage.setItem(CACHE_KEY, JSON.stringify(value));
    else localStorage.removeItem(CACHE_KEY);
  } catch {
    // Sin almacenamiento local: la pantalla de carga queda con los colores de Centinela.
  }
}

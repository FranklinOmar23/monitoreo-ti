import type { MachineType } from '@monitoreoti/shared';
import type { IconName } from '@components/Icon';

/** "hace 3 min", "hace 2 h", "hace 4 d"; null o fecha futura → "—" / "ahora". */
export function timeAgo(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return '—';
  const seconds = Math.round((now - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return 'ahora';
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `hace ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `hace ${hours} h`;
  return `hace ${Math.floor(hours / 24)} d`;
}

/** Reporte atrasado (más de 10 min): se resalta en la lista (regla "sin reportar", sección 14). */
export function isLate(iso: string | null | undefined, now: number = Date.now()): boolean {
  return !iso || now - new Date(iso).getTime() > 10 * 60_000;
}

const TYPE_LABELS: Record<MachineType, string> = {
  computadora: 'PC',
  laptop: 'Laptop',
  servidor: 'Servidor',
  impresora: 'Impresora',
  otro: 'Otro',
};

const TYPE_ICONS: Record<MachineType, IconName> = {
  computadora: 'monitor',
  laptop: 'laptop',
  servidor: 'database',
  impresora: 'printer',
  otro: 'network',
};

export const typeLabel = (type: MachineType) => TYPE_LABELS[type] ?? type;
export const typeIcon = (type: MachineType): IconName => TYPE_ICONS[type] ?? 'monitor';
export const MACHINE_TYPES = Object.keys(TYPE_LABELS) as MachineType[];

/** Número redondeado a entero para mostrar porcentajes; null → null. */
export const whole = (value: number | null | undefined): number | null =>
  value === null || value === undefined ? null : Math.round(value);

/** "11.4 / 16 GB": un decimal por debajo de 100 GB, entero por encima. */
export function usedOfTotal(usedGb: number, totalGb: number): string {
  const fmt = (gb: number) => (gb < 100 ? gb.toFixed(1).replace(/\.0$/, '') : Math.round(gb).toString());
  return `${fmt(usedGb)} / ${fmt(totalGb)} GB`;
}

/** "800 MB", "1.25 GB". */
export function megabytes(mb: number | null): string {
  if (mb === null) return '—';
  return mb >= 1024 ? `${(mb / 1024).toFixed(2)} GB` : `${Math.round(mb)} MB`;
}

/** "LUN 13" para el eje de la gráfica de 7 días; "HOY" para hoy. */
export function dayLabel(isoDate: string, today: Date = new Date()): string {
  const [year, month, day] = isoDate.split('-').map(Number);
  const date = new Date(year!, month! - 1, day!);
  if (date.toDateString() === today.toDateString()) return 'HOY';
  const weekday = date.toLocaleDateString('es-DO', { weekday: 'short' }).replace('.', '').toUpperCase();
  return `${weekday.slice(0, 3)} ${day}`;
}

/** Fecha y hora local corta: "27/10/2026 09:15". */
export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString('es-DO', { dateStyle: 'short', timeStyle: 'short' });
}

/** Hora local "09:15" para el eje de las gráficas. */
export function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString('es-DO', { hour: '2-digit', minute: '2-digit', hour12: false });
}

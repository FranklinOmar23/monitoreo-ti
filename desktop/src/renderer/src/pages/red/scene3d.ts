import type { DeviceStatus, RackDevice, RackItem } from '@monitoreoti/shared';

/** Medidas reales aproximadas, en metros: una U mide 44.45 mm; un gabinete estándar, 60 × 100 cm. */
export const U_METERS = 0.04445;
export const RACK_WIDTH = 0.6;
export const RACK_DEPTH = 1.0;
/** Pasillo entre filas de gabinetes. */
export const AISLE = 1.2;

export const STATUS_COLOR: Record<DeviceStatus, string> = {
  en_linea: '#2f9e6f',
  advertencia: '#e8971a',
  critico: '#d9452f',
  sin_respuesta: '#aaa69d',
  pasivo: '#5b6770',
  baja: '#cfcac0',
};

type RackPlace = Pick<RackItem, 'id' | 'name' | 'row' | 'position' | 'heightUnits'>;

/** Una fila del plano: los gabinetes con su columna (las posiciones vacías quedan como hueco). */
export interface PlanRow {
  row: string | null;
  racks: { rack: RackPlace; column: number }[];
}

/**
 * Filas del plano de la sala (W-59): por la fila indicada (A, B, …) y, dentro, por la posición; los gabinetes sin
 * fila van al final en su propia fila. Sin posición, toman la siguiente columna libre.
 */
export function planRows<T extends RackPlace>(racks: readonly T[]): PlanRow[] {
  const byRow = new Map<string | null, T[]>();
  for (const rack of racks) {
    const key = rack.row?.trim() ? rack.row.trim().toUpperCase() : null;
    byRow.set(key, [...(byRow.get(key) ?? []), rack]);
  }
  const keys = [...byRow.keys()].sort((a, b) =>
    a === null ? 1 : b === null ? -1 : a.localeCompare(b, 'es', { numeric: true }),
  );
  return keys.map((row) => {
    const sorted = [...byRow.get(row)!].sort(
      (a, b) =>
        (a.position ?? Number.MAX_SAFE_INTEGER) - (b.position ?? Number.MAX_SAFE_INTEGER) ||
        a.name.localeCompare(b.name),
    );
    const used = new Set<number>();
    const placed = sorted.map((rack) => {
      let column = rack.position && rack.position > 0 && !used.has(rack.position) ? rack.position : 1;
      while (used.has(column)) column++;
      used.add(column);
      return { rack, column };
    });
    return { row, racks: placed };
  });
}

export interface PlacedRack {
  id: number;
  name: string;
  /** Centro del gabinete en el piso (x a lo largo de la fila, z de fila en fila). */
  x: number;
  z: number;
  height: number;
}

/** Gabinetes de la sala en 3D (W-43), con el mismo orden que el plano. */
export function layoutRoom(racks: readonly RackPlace[]): PlacedRack[] {
  return planRows(racks).flatMap((row, rowIndex) =>
    row.racks.map(({ rack, column }) => ({
      id: rack.id,
      name: rack.name,
      x: (column - 1) * (RACK_WIDTH + 0.05),
      z: rowIndex * (RACK_DEPTH + AISLE),
      height: rack.heightUnits * U_METERS,
    })),
  );
}

export interface DeviceBox {
  id: number;
  name: string;
  /** Centro vertical desde el piso del gabinete. */
  y: number;
  height: number;
  color: string;
}

/** Equipos montados como cajas a su altura real (W-44): la U inferior es la que se registra. */
export function deviceBoxes(devices: readonly RackDevice[]): DeviceBox[] {
  return devices.map((d) => {
    const height = Math.max(d.heightUnits, 1) * U_METERS;
    return {
      id: d.id,
      name: d.name,
      y: (d.positionU - 1) * U_METERS + height / 2,
      height: height - 0.003,
      color: d.portsWithProblems > 0 && d.status === 'en_linea' ? STATUS_COLOR.advertencia : STATUS_COLOR[d.status],
    };
  });
}

/** El estado más grave de los equipos de un gabinete (para pintar el gabinete en la vista de sala). */
export function worstStatus(devices: readonly RackDevice[]): DeviceStatus | null {
  const order: DeviceStatus[] = ['critico', 'sin_respuesta', 'advertencia', 'en_linea', 'pasivo', 'baja'];
  return order.find((s) => devices.some((d) => d.status === s)) ?? null;
}

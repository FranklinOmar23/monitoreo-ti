import type { DeviceStatus, RackDevice, RackItem, RackPlacementInput } from '@monitoreoti/shared';

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
  /** Giro sobre el eje vertical: las filas se miran de a pares (A con B, C con D), con el pasillo frío entre ellas. */
  rotation: number;
  row: string | null;
}

/** Distancia entre gabinetes vecinos de una fila y entre filas (con el pasillo). */
export const SLOT = RACK_WIDTH + 0.05;
export const ROW_PITCH = RACK_DEPTH + AISLE;

/** Gabinetes de la sala en 3D (W-43), con el mismo orden que el plano. */
export function layoutRoom(racks: readonly RackPlace[]): PlacedRack[] {
  return planRows(racks).flatMap((row, rowIndex) =>
    row.racks.map(({ rack, column }) => ({
      id: rack.id,
      name: rack.name,
      x: (column - 1) * SLOT,
      z: rowIndex * ROW_PITCH,
      height: rack.heightUnits * U_METERS,
      rotation: rowIndex % 2 === 1 ? Math.PI : 0,
      row: row.row,
    })),
  );
}

/** Las filas con nombre, en orden, con dónde quedan en el piso (para sus nombres y las vistas de frente). */
export function rowBands(
  racks: readonly RackPlace[],
): { name: string; z: number; from: number; to: number; facing: 1 | -1 }[] {
  return planRows(racks)
    .map((row, index) => ({ row, index }))
    .filter(({ row }) => row.row !== null)
    .map(({ row, index }) => {
      const columns = row.racks.map((r) => r.column);
      return {
        name: row.row!,
        z: index * ROW_PITCH,
        from: (Math.min(...columns) - 1) * SLOT,
        to: (Math.max(...columns) - 1) * SLOT,
        facing: index % 2 === 1 ? -1 : 1,
      };
    });
}

/** El nombre de la fila siguiente: después de C, D; después de 3, 4; si no, la cantidad más uno. */
export function nextRowName(names: readonly string[]): string {
  if (names.length === 0) return 'A';
  if (names.every((n) => /^[A-Z]$/.test(n))) {
    const last = names.reduce((a, b) => (a > b ? a : b));
    return last === 'Z' ? `${names.length + 1}` : String.fromCharCode(last.charCodeAt(0) + 1);
  }
  if (names.every((n) => /^\d+$/.test(n))) return String(Math.max(...names.map(Number)) + 1);
  return String(names.length + 1);
}

/** Un lugar del plano: la fila y la posición (columna, desde 1). */
export interface Cell {
  row: string;
  position: number;
}

/** La celda bajo un punto del piso del 3D. Más allá de la última fila se ofrece una fila nueva (solo una). */
export function cellAt(racks: readonly RackPlace[], x: number, z: number): Cell {
  const names = planRows(racks).flatMap((r) => (r.row ? [r.row] : []));
  const index = Math.max(0, Math.round(z / ROW_PITCH));
  return {
    row: index < names.length ? names[index]! : nextRowName(names),
    position: Math.max(1, Math.round(x / SLOT) + 1),
  };
}

/** El centro en el piso de una celda (para mostrar dónde va a quedar el gabinete que se arrastra). */
export function cellCenter(racks: readonly RackPlace[], cell: Cell): { x: number; z: number } {
  const names = planRows(racks).flatMap((r) => (r.row ? [r.row] : []));
  const index = names.indexOf(cell.row);
  return { x: (cell.position - 1) * SLOT, z: (index < 0 ? names.length : index) * ROW_PITCH };
}

/** ¿Qué gabinete está en esa celda? (según el plano, con las posiciones que faltan ya resueltas). */
export function occupantOf(racks: readonly RackPlace[], cell: Cell): number | null {
  for (const row of planRows(racks)) {
    if (row.row !== cell.row) continue;
    return row.racks.find((r) => r.column === cell.position)?.rack.id ?? null;
  }
  return null;
}

/**
 * Mover un gabinete a otra celda (o sacarlo del plano con null). Si la celda está ocupada, los dos se intercambian;
 * desde «Sin ubicar» no se puede soltar sobre otro (devuelve null). Devuelve lo que hay que guardar: el gabinete movido
 * y todos los de las filas tocadas cuya posición guardada no era la que se veía (así el plano no se reacomoda solo).
 */
export function moveRack(racks: readonly RackPlace[], id: number, target: Cell | null): RackPlacementInput[] | null {
  const where = new Map<number, Cell | null>();
  for (const row of planRows(racks))
    for (const { rack, column } of row.racks) where.set(rack.id, row.row ? { row: row.row, position: column } : null);
  const from = where.get(id);
  if (from === undefined) return null;
  if (target && from && from.row === target.row && from.position === target.position) return [];

  const occupant = target ? occupantOf(racks, target) : null;
  if (occupant !== null && occupant !== id) {
    if (!from) return null;
    where.set(occupant, from);
  }
  where.set(id, target ? { row: target.row.trim().toUpperCase(), position: target.position } : null);

  const touched = new Set([from?.row, target?.row.trim().toUpperCase()].filter(Boolean));
  return racks.flatMap((rack) => {
    const cell = where.get(rack.id) ?? null;
    const storedRow = rack.row?.trim() ? rack.row.trim().toUpperCase() : null;
    const moved = rack.id === id || rack.id === occupant;
    const inTouchedRow = cell !== null && touched.has(cell.row);
    const differs = (cell?.row ?? null) !== storedRow || (cell?.position ?? null) !== (rack.position ?? null);
    return (moved || inTouchedRow) && differs
      ? [{ gabineteId: rack.id, fila: cell?.row ?? null, posicion: cell?.position ?? null }]
      : [];
  });
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

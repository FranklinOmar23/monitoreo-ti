/**
 * Estados y colores del semáforo usados en todo el sistema.
 * Los valores coinciden con los CHECK de la base de datos.
 * Los colores de estado se usan solo para estado (sección 12.4).
 */

export const MACHINE_STATUSES = ['sano', 'advertencia', 'critico', 'desconectado', 'baja'] as const;
export type MachineStatus = (typeof MACHINE_STATUSES)[number];

export const PORT_STATUSES = ['conectado', 'libre', 'errores', 'caido', 'deshabilitado'] as const;
export type PortStatus = (typeof PORT_STATUSES)[number];

export const STATUS_COLORS = {
  sano: '#16a34a',
  advertencia: '#eab308',
  critico: '#dc2626',
  desconectado: '#6b7280',
  baja: '#9ca3af',
} as const satisfies Record<MachineStatus, string>;

export const STATUS_LABELS = {
  sano: 'Sano',
  advertencia: 'Advertencia',
  critico: 'Crítico',
  desconectado: 'Desconectado',
  baja: 'De baja',
} as const satisfies Record<MachineStatus, string>;

/** Puertos: verde conectado, gris libre, amarillo con errores, rojo caído, azul troncal. */
export const PORT_COLORS = {
  conectado: '#16a34a',
  libre: '#9ca3af',
  errores: '#eab308',
  caido: '#dc2626',
  deshabilitado: '#4b5563',
  troncal: '#2563eb',
} as const satisfies Record<PortStatus | 'troncal', string>;

export function portColor(status: PortStatus, isTrunk: boolean): string {
  return isTrunk && status === 'conectado' ? PORT_COLORS.troncal : PORT_COLORS[status];
}

/** Orden de gravedad para ordenar listas y elegir el peor estado de un grupo. */
const SEVERITY: Record<MachineStatus, number> = {
  critico: 4,
  advertencia: 3,
  desconectado: 2,
  sano: 1,
  baja: 0,
};

export function worstStatus(statuses: readonly MachineStatus[]): MachineStatus | undefined {
  return statuses.reduce<MachineStatus | undefined>(
    (worst, current) => (worst === undefined || SEVERITY[current] > SEVERITY[worst] ? current : worst),
    undefined,
  );
}

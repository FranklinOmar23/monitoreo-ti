import type { AttentionCode, SummaryKpi, SummaryKpiKey } from '@monitoreoti/shared';
import type { Better, Tone } from './widgets';

/** Cómo se lee cada indicador del resumen (decisión 92). */
export interface KpiMeta {
  label: string;
  unit?: string;
  better: Better;
  /** El reporte que lo explica. */
  report: string;
  tone?: (value: number) => Tone;
}

export const KPI_META: Record<SummaryKpiKey, KpiMeta> = {
  disponibilidad: {
    label: 'Disponibilidad',
    unit: '%',
    better: 'up',
    report: 'disponibilidad',
    tone: (v) => (v >= 95 ? 'sano' : v >= 90 ? 'advertencia' : 'critico'),
  },
  equipos: { label: 'Equipos', better: 'none', report: 'inventario' },
  alertas: { label: 'Alertas', better: 'down', report: 'alertas' },
  alertas_criticas: {
    label: 'Alertas críticas',
    better: 'down',
    report: 'alertas',
    tone: (v) => (v > 0 ? 'critico' : 'sano'),
  },
  resolucion_alertas: { label: 'Resolución de alertas', unit: 'h', better: 'down', report: 'alertas' },
  discos_riesgo: {
    label: 'Equipos con discos en riesgo',
    better: 'down',
    report: 'discos-seguridad',
    tone: (v) => (v > 0 ? 'advertencia' : 'sano'),
  },
  seguridad: {
    label: 'Equipos con riesgo de seguridad',
    better: 'down',
    report: 'discos-seguridad',
    tone: (v) => (v > 0 ? 'critico' : 'sano'),
  },
  cpu: { label: 'CPU promedio', unit: '%', better: 'down', report: 'rendimiento' },
  ram: {
    label: 'RAM promedio',
    unit: '%',
    better: 'down',
    report: 'rendimiento',
    tone: (v) => (v >= 85 ? 'advertencia' : 'neutral'),
  },
  tickets_creados: { label: 'Tickets creados', better: 'none', report: 'tickets' },
  tickets_abiertos: {
    label: 'Tickets abiertos hoy',
    better: 'down',
    report: 'tickets',
    tone: (v) => (v > 0 ? 'advertencia' : 'neutral'),
  },
  resolucion_tickets: { label: 'Solución de tickets', unit: 'h', better: 'down', report: 'tickets' },
  sesiones_remotas: { label: 'Sesiones remotas', better: 'none', report: 'remoto' },
  horas_remotas: { label: 'Horas en remoto', unit: 'h', better: 'none', report: 'remoto' },
};

export const BASIC_KPIS: readonly SummaryKpiKey[] = [
  'disponibilidad',
  'alertas_criticas',
  'resolucion_alertas',
  'discos_riesgo',
  'seguridad',
  'alertas',
  'equipos',
];

export const ADVANCED_KPIS: readonly SummaryKpiKey[] = [
  'cpu',
  'ram',
  'tickets_abiertos',
  'resolucion_tickets',
  'tickets_creados',
  'sesiones_remotas',
  'horas_remotas',
];

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/** La frase de cada punto de "Requiere atención". */
export function attentionText(code: AttentionCode, count: number): string {
  switch (code) {
    case 'disco_falla':
      return `${plural(count, 'equipo', 'equipos')} con un disco que reporta fallas`;
    case 'sin_proteccion':
      return `${plural(count, 'equipo', 'equipos')} sin antivirus o con el firewall apagado`;
    case 'disco_lleno':
      return `${plural(count, 'equipo', 'equipos')} con un disco casi lleno (menos de 15% libre)`;
    case 'antivirus_desactualizado':
      return `${plural(count, 'equipo', 'equipos')} con el antivirus desactualizado`;
    case 'baja_disponibilidad':
      return `${plural(count, 'equipo', 'equipos')} en línea menos del 90% del tiempo`;
    case 'alertas_abiertas':
      return `${plural(count, 'alerta del periodo sigue abierta', 'alertas del periodo siguen abiertas')}`;
    case 'ampliar_ram':
      return `${plural(count, 'equipo', 'equipos')} con la memoria al límite: conviene ampliar la RAM`;
    case 'revisar_cpu':
      return `${plural(count, 'equipo', 'equipos')} con el procesador al límite`;
    case 'mantenimientos_vencidos':
      return `${plural(count, 'mantenimiento vencido', 'mantenimientos vencidos')} sin hacer`;
  }
}

export const findKpi = (kpis: readonly SummaryKpi[] | undefined, key: SummaryKpiKey) =>
  kpis?.find((k) => k.key === key);

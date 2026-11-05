/**
 * Protocolo agente <-> servidor (sección 9). Mensajes JSON sobre WebSocket con este sobre.
 * El servidor tiene su contraparte en C#; los nombres de campo se mantienen en español
 * porque forman parte del contrato del protocolo.
 */

export const PROTOCOL_VERSION = 1;

export const AGENT_MESSAGE_TYPES = [
  'hola',
  'heartbeat',
  'metricas',
  'inventario_hardware',
  'inventario_software',
  'interfaces_red',
  'eventos',
  'procesos',
  'servicios',
  'seguridad',
  'volumenes',
  'salud_discos',
  'resultado_comando',
  'snmp_resultado',
  'barrido_ip_resultado',
  'sesion_lista',
  'error_agente',
] as const;
export type AgentMessageType = (typeof AGENT_MESSAGE_TYPES)[number];

export const SERVER_MESSAGE_TYPES = [
  'ack',
  'bienvenida',
  'ejecutar_comando',
  'abrir_sesion_remota',
  'cerrar_sesion_remota',
  'actualizar_agente',
  'solicitar_inventario',
  'config_actualizada',
  'barrido_ip',
  'sondear_ahora',
] as const;
export type ServerMessageType = (typeof SERVER_MESSAGE_TYPES)[number];

export interface Envelope<TType extends string = string, TData = unknown> {
  v: number;
  tipo: TType;
  id: string;
  /** ISO 8601 en UTC. */
  ts: string;
  datos: TData;
}

export function createEnvelope<TType extends string, TData>(
  tipo: TType,
  datos: TData,
  options: { id?: string; now?: Date } = {},
): Envelope<TType, TData> {
  return {
    v: PROTOCOL_VERSION,
    tipo,
    id: options.id ?? randomUuid(),
    ts: (options.now ?? new Date()).toISOString(),
    datos,
  };
}

// Web Crypto existe en Node 19+, Electron y navegadores; este paquete no depende de los tipos DOM.
function randomUuid(): string {
  const webCrypto = (globalThis as { crypto?: { randomUUID?: () => string } }).crypto;
  if (!webCrypto?.randomUUID) throw new Error('crypto.randomUUID no está disponible en este entorno.');
  return webCrypto.randomUUID();
}

export function isEnvelope(value: unknown): value is Envelope {
  if (typeof value !== 'object' || value === null) return false;
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate.v === 'number' &&
    typeof candidate.tipo === 'string' &&
    typeof candidate.id === 'string' &&
    typeof candidate.ts === 'string' &&
    'datos' in candidate
  );
}

// ---------------------------------------------------------------------------
// Cargas útiles (campo "datos") de los mensajes que ya envía el agente (Fase 1).
// ---------------------------------------------------------------------------

/** Mensaje "hola": abre cada conexión. */
export interface HolaDatos {
  version: string;
  hostname: string;
  so: string;
  es_recolector: boolean;
}

/** Mensaje "bienvenida": configuración que el servidor entrega al agente. */
export interface BienvenidaDatos {
  heartbeat_s: number;
  metricas_s: number;
  es_recolector: boolean;
}

/** Mensaje "metricas": porcentajes de 0 a 100 y grados Celsius; null cuando el dato no está disponible. */
export interface MetricasDatos {
  cpu: number | null;
  ram: number | null;
  disco: number | null;
  temperatura: number | null;
}

export type SaludComponente = 'sano' | 'advertencia' | 'critico' | 'desconocido';

export interface ComponenteDatos {
  /** cpu, ram, disco, gpu, placa_base, bios, bateria. */
  tipo: string;
  descripcion: string;
  capacidad?: string;
  estado_salud: SaludComponente;
}

/** Mensaje "inventario_hardware": se reemplaza por completo el inventario de componentes de la máquina. */
export interface InventarioHardwareDatos {
  fabricante?: string;
  modelo?: string;
  serie?: string;
  tipo: 'computadora' | 'laptop' | 'servidor';
  sistema_operativo?: string;
  /** Memoria instalada en MB (para mostrar "usado / total"). */
  ram_total_mb?: number;
  componentes: ComponenteDatos[];
}

export interface ProgramaDatos {
  nombre: string;
  version?: string;
  editor?: string;
  /** AAAA-MM-DD. */
  fecha_instalacion?: string;
}

/** Mensaje "inventario_software": lista completa de programas instalados. */
export interface InventarioSoftwareDatos {
  programas: ProgramaDatos[];
}

export type TipoInterfaz = 'cableada' | 'wifi' | 'virtual' | 'otra';

export interface InterfazRedDatos {
  nombre: string;
  /** AA:BB:CC:DD:EE:FF. */
  mac: string;
  ip?: string;
  tipo: TipoInterfaz;
  velocidad_mbps?: number;
}

/** Mensaje "interfaces_red": lista completa de tarjetas de red del equipo. */
export interface InterfacesRedDatos {
  interfaces: InterfazRedDatos[];
}

// ---------------------------------------------------------------------------
// Fase 3: volúmenes, procesos, servicios, eventos, salud de discos y seguridad.
// ---------------------------------------------------------------------------

/** Mensaje "volumenes": espacio de cada unidad fija (cada 10 min). */
export interface VolumenesDatos {
  volumenes: { unidad: string; etiqueta?: string; total_gb: number; libre_gb: number }[];
}

/** Mensaje "procesos": los que más consumen (cada 5 min). cpu en % del equipo completo. */
export interface ProcesosDatos {
  total: number;
  procesos: { nombre: string; pid: number; cpu: number; ram_mb: number }[];
}

export type EstadoServicio = 'en_ejecucion' | 'detenido' | 'otro';
export type InicioServicio = 'automatico' | 'automatico_retrasado' | 'manual' | 'deshabilitado';

/** Mensaje "servicios": servicios con inicio automático (cada 5 min). */
export interface ServiciosDatos {
  servicios: {
    nombre: string;
    nombre_visible?: string;
    estado: EstadoServicio;
    inicio: InicioServicio;
    /** Código con el que terminó (solo si está detenido): 0 = se detuvo bien, otro = falló. */
    codigo_salida?: number;
  }[];
}

/** Mensaje "eventos": errores y críticos nuevos de los registros Sistema y Aplicación (cada minuto). */
export interface EventosDatos {
  eventos: {
    registro: 'System' | 'Application';
    id_registro: number;
    /** ISO 8601 en UTC. */
    fecha: string;
    origen: string;
    codigo: number;
    nivel: 'critico' | 'error';
    mensaje?: string;
  }[];
}

/** Mensaje "salud_discos": SMART y contadores de confiabilidad (cada 6 h). */
export interface SaludDiscosDatos {
  discos: {
    modelo: string;
    serie?: string;
    tamano_gb?: number;
    salud: SaludComponente;
    sectores_reasignados?: number;
    sectores_pendientes?: number;
    errores_lectura?: number;
    /** Desgaste en % (SSD). */
    desgaste?: number;
    temperatura?: number;
    horas_encendido?: number;
  }[];
}

/** Mensaje "seguridad": antivirus registrados en el Centro de seguridad y firewall (cada 6 h). */
export interface SeguridadDatos {
  antivirus: { nombre: string; activo: boolean; actualizado: boolean }[];
  firewall_activo?: boolean;
}

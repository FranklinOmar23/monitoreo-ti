import { networkInterfaces } from 'node:os';
import {
  createEnvelope,
  type Envelope,
  type InterfacesRedDatos,
  type InventarioHardwareDatos,
  type InventarioSoftwareDatos,
  type MetricasDatos,
} from '@monitoreoti/shared';
import type { ServerWelcome } from './gateway-client';
import type { Outbox } from './outbox';
import { Sender } from './sender';

export type Log = (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;

export interface Collectors {
  metrics: () => Promise<MetricasDatos>;
  hardware: () => Promise<InventarioHardwareDatos>;
  software: () => Promise<InventarioSoftwareDatos>;
  interfaces: (ipByName: ReadonlyMap<string, string>) => Promise<InterfacesRedDatos>;
}

/** Recolectores de salud (Fase 3). Opcionales: cada uno se programa solo si existe. */
export interface HealthCollectors {
  volumes?: () => Promise<unknown>;
  processes?: () => Promise<unknown>;
  services?: () => Promise<unknown>;
  events?: () => Promise<unknown>;
  diskHealth?: () => Promise<unknown>;
  security?: () => Promise<unknown>;
}

/**
 * Cada recolector de salud: tipo de mensaje, primer envío tras el arranque y frecuencia (sección 8.2).
 * Los primeros envíos se escalonan para no lanzar varios PowerShell a la vez.
 */
export const HEALTH_SCHEDULE: { key: keyof HealthCollectors; tipo: string; firstMs: number; everyMs: number }[] = [
  { key: 'events', tipo: 'eventos', firstMs: 20_000, everyMs: 60_000 },
  { key: 'volumes', tipo: 'volumenes', firstMs: 30_000, everyMs: 10 * 60_000 },
  { key: 'processes', tipo: 'procesos', firstMs: 40_000, everyMs: 5 * 60_000 },
  { key: 'services', tipo: 'servicios', firstMs: 50_000, everyMs: 5 * 60_000 },
  { key: 'diskHealth', tipo: 'salud_discos', firstMs: 60_000, everyMs: 6 * 60 * 60_000 },
  { key: 'security', tipo: 'seguridad', firstMs: 70_000, everyMs: 6 * 60 * 60_000 },
];

export interface Gateway {
  start(): void;
  stop(): void;
  request(envelope: Envelope): Promise<void>;
}

export interface RuntimeOptions {
  outbox: Outbox;
  collectors: Collectors;
  health?: HealthCollectors;
  /** Crea la conexión con el servidor; las pruebas inyectan una falsa. */
  createGateway: (callbacks: { onReady: (welcome: ServerWelcome) => void }) => Gateway;
  log: Log;
  /** Escalonan el arranque de los inventarios (PowerShell pesa) para no competir con el inicio del equipo. */
  startupDelaysMs?: { hardware: number; software: number; interfaces: number };
}

const HOUR = 60 * 60_000;
export const HARDWARE_EVERY_MS = 24 * HOUR;
export const SOFTWARE_EVERY_MS = 24 * HOUR;
export const INTERFACES_EVERY_MS = 6 * HOUR;
const INTERFACE_CHANGE_CHECK_MS = 5 * 60_000;
const DEFAULT_METRICS_SECONDS = 60;

/**
 * Orquesta el agente: recolecta en sus horarios, guarda en la cola local y la vacía por la conexión.
 * La recolección no depende de la red: sin conexión, los mensajes esperan en la cola.
 */
export class AgentRuntime {
  private readonly gateway: Gateway;
  private readonly sender: Sender;
  private readonly timers = new Set<ReturnType<typeof setTimeout> | ReturnType<typeof setInterval>>();
  private metricsTimer: ReturnType<typeof setInterval> | undefined;
  private metricsSeconds = DEFAULT_METRICS_SECONDS;
  private networkSignature = '';
  private stopped = false;

  constructor(private readonly options: RuntimeOptions) {
    this.gateway = options.createGateway({ onReady: (welcome) => this.onReady(welcome) });
    this.sender = new Sender(options.outbox, this.gateway, (message, extra) => options.log('info', message, extra));
  }

  start(): void {
    const delays = this.options.startupDelaysMs ?? { hardware: 5_000, software: 15_000, interfaces: 25_000 };
    this.networkSignature = currentNetworkSignature();

    this.scheduleMetrics();
    this.schedule('hardware', delays.hardware, HARDWARE_EVERY_MS, () => this.collectHardware());
    this.schedule('software', delays.software, SOFTWARE_EVERY_MS, () => this.collectSoftware());
    this.schedule('interfaces', delays.interfaces, INTERFACES_EVERY_MS, () => this.collectInterfaces());
    this.every(INTERFACE_CHANGE_CHECK_MS, () => this.checkNetworkChange());

    for (const item of HEALTH_SCHEDULE) {
      const collect = this.options.health?.[item.key];
      if (!collect) continue;
      this.schedule(item.tipo, item.firstMs, item.everyMs, async () => {
        const datos = await collect();
        // Sin eventos nuevos no se manda nada: el mensaje de eventos es incremental.
        if (item.key === 'events' && (datos as { eventos?: unknown[] }).eventos?.length === 0) return;
        this.submit(createEnvelope(item.tipo, datos));
      });
    }

    this.gateway.start();
  }

  stop(): void {
    this.stopped = true;
    for (const timer of this.timers) {
      clearTimeout(timer);
      clearInterval(timer);
    }
    this.timers.clear();
    this.gateway.stop();
  }

  private onReady(welcome: ServerWelcome): void {
    if (welcome.metricsSeconds !== this.metricsSeconds) {
      this.metricsSeconds = welcome.metricsSeconds;
      this.scheduleMetrics();
    }
    void this.sender.drain();
  }

  private scheduleMetrics(): void {
    if (this.metricsTimer) {
      clearInterval(this.metricsTimer);
      this.timers.delete(this.metricsTimer);
    }
    this.metricsTimer = this.every(this.metricsSeconds * 1000, () => this.collectMetrics());
  }

  private async collectMetrics(): Promise<void> {
    this.submit(createEnvelope('metricas', await this.options.collectors.metrics()));
  }

  private async collectHardware(): Promise<void> {
    this.submit(createEnvelope('inventario_hardware', await this.options.collectors.hardware()));
  }

  private async collectSoftware(): Promise<void> {
    this.submit(createEnvelope('inventario_software', await this.options.collectors.software()));
  }

  private async collectInterfaces(): Promise<void> {
    this.networkSignature = currentNetworkSignature();
    this.submit(createEnvelope('interfaces_red', await this.options.collectors.interfaces(ipsByName())));
  }

  /** Una tarjeta nueva, un cable conectado o un cambio de IP se reporta sin esperar a las 6 h. */
  private async checkNetworkChange(): Promise<void> {
    if (currentNetworkSignature() !== this.networkSignature) await this.collectInterfaces();
  }

  private submit(envelope: Envelope): void {
    if (!this.stopped) this.sender.submit(envelope);
  }

  private every(ms: number, task: () => Promise<void>): ReturnType<typeof setInterval> {
    const timer = setInterval(() => void this.guarded(task), ms);
    this.timers.add(timer);
    return timer;
  }

  /** Primera ejecución tras un retraso y luego periódica. */
  private schedule(name: string, delayMs: number, everyMs: number, task: () => Promise<void>): void {
    const first = setTimeout(() => void this.guarded(task, name), delayMs);
    this.timers.add(first);
    this.every(everyMs, () => this.guarded(task, name));
  }

  private async guarded(task: () => Promise<void>, name = 'tarea'): Promise<void> {
    try {
      await task();
    } catch (error) {
      // Un recolector que falla (WMI caído, por ejemplo) no debe tumbar el servicio ni a los demás.
      this.options.log('error', `Falló la recolección de ${name}`, { error: String(error) });
    }
  }
}

function currentNetworkSignature(): string {
  return JSON.stringify(
    Object.entries(networkInterfaces())
      .map(([name, addresses]) => [name, (addresses ?? []).map((a) => `${a.mac}/${a.address}`).sort()])
      .sort(),
  );
}

/** Primera IPv4 de cada adaptador, por nombre (el mismo que usa Get-NetAdapter). */
function ipsByName(): Map<string, string> {
  const result = new Map<string, string>();
  for (const [name, addresses] of Object.entries(networkInterfaces())) {
    const ipv4 = addresses?.find((a) => a.family === 'IPv4' && !a.internal);
    if (ipv4) result.set(name, ipv4.address);
  }
  return result;
}

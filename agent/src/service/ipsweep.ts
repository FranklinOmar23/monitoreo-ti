import { createEnvelope, type Envelope } from '@monitoreoti/shared';
import { hostJson } from './snmp';

/** Lo que pide el servidor en "barrido_ip" (decisión 72). */
export interface IpSweepRequest {
  subnetId: number;
  ips: string[];
  /** Dispositivo (el firewall) cuya ARP se lee tras el barrido; lo consulta este mismo recolector. */
  arpDeviceId: number | null;
}

export interface ArpEntry {
  ip: string;
  mac: string;
  /** Interfaz del equipo de red donde está la entrada (dice la VLAN). */
  ifIndex?: number;
}

/** Nombre de un equipo visto en el barrido (decisión 75). */
export interface HostNameEntry {
  ip: string;
  nombre: string;
  origen: 'netbios' | 'dns';
}

/** Lo que devuelve el anfitrión C# ("MonitoreoTI.AgentHost.exe ping"). */
export interface HostSweepResult {
  ok: boolean;
  error?: string;
  responden?: string[];
  arp?: ArpEntry[];
  nombres?: HostNameEntry[];
}

export type HostSweep = (ips: string[]) => Promise<HostSweepResult>;

/** Hasta una /22: lo mismo que acepta el servidor. */
const MAX_IPS = 1024;
const IPV4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;

/** Lee el pedido del servidor; null si no es válido. */
export function parseSweepRequest(value: unknown): IpSweepRequest | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const ips = Array.isArray(v['ips'])
    ? v['ips'].filter((ip): ip is string => typeof ip === 'string' && IPV4.test(ip))
    : [];
  if (typeof v['subred_id'] !== 'number' || ips.length === 0 || ips.length > MAX_IPS) return null;
  const arp = v['arp_dispositivo_id'];
  return { subnetId: v['subred_id'], ips, arpDeviceId: typeof arp === 'number' ? arp : null };
}

/** Barrido con el anfitrión C#: un ping a cada dirección y la ARP de este equipo. */
export function hostIpSweep(hostExe: string | undefined, timeoutMs = 300_000): HostSweep {
  return (ips) => hostJson<HostSweepResult>(hostExe, 'ping', { ips }, timeoutMs, 'El barrido de IP');
}

export interface IpSweeperOptions {
  sweep: HostSweep;
  /** ARP de un dispositivo que consulta este recolector (null si no lo tiene o no responde). */
  arpOf: (deviceId: number) => Promise<ArpEntry[] | null>;
  submit: (envelope: Envelope) => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
}

/**
 * Barridos de IP del recolector (decisión 72): de a uno, para no saturar la red. Tras los pings lee la ARP del firewall:
 * aunque una PC no conteste el ping (firewall de Windows), el firewall tuvo que buscar su MAC para entregárselo.
 */
export class IpSweeper {
  private queue: Promise<void> = Promise.resolve();

  constructor(private readonly options: IpSweeperOptions) {}

  /** Atiende un "barrido_ip"; devuelve cuando termina (las pruebas lo esperan). */
  handle(datos: unknown): Promise<void> {
    const request = parseSweepRequest(datos);
    if (!request) {
      this.options.log('error', 'Pedido de barrido de IP inválido; se ignora.');
      return Promise.resolve();
    }
    this.queue = this.queue.then(() => this.run(request));
    return this.queue;
  }

  private async run(request: IpSweepRequest): Promise<void> {
    const started = Date.now();
    const result = await this.options.sweep(request.ips);
    let arp = result.arp ?? [];
    if (result.ok && request.arpDeviceId !== null) {
      const remote = await this.options.arpOf(request.arpDeviceId);
      if (remote) arp = [...arp, ...remote];
    }
    this.options.log(result.ok ? 'info' : 'error', 'Barrido de IP terminado', {
      subred: request.subnetId,
      direcciones: request.ips.length,
      responden: result.responden?.length ?? 0,
      arp: arp.length,
      nombres: result.nombres?.length ?? 0,
      segundos: Math.round((Date.now() - started) / 1000),
      error: result.error,
    });
    this.options.submit(
      createEnvelope('barrido_ip_resultado', {
        subredId: request.subnetId,
        ok: result.ok,
        error: result.error,
        responden: result.responden ?? [],
        arp,
        nombres: result.nombres ?? [],
      }),
    );
  }
}

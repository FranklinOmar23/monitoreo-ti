import { statfs } from 'node:fs/promises';
import { cpus, freemem, totalmem } from 'node:os';
import type { MetricasDatos } from '@monitoreoti/shared';

interface CpuTimes {
  idle: number;
  total: number;
}

export function readCpuTimes(): CpuTimes {
  let idle = 0;
  let total = 0;
  for (const { times } of cpus()) {
    idle += times.idle;
    total += times.user + times.nice + times.sys + times.idle + times.irq;
  }
  return { idle, total };
}

/** Uso de CPU entre dos lecturas de tiempos acumulados; null si no pasó tiempo entre ellas. */
export function cpuPercent(previous: CpuTimes, current: CpuTimes): number | null {
  const total = current.total - previous.total;
  if (total <= 0) return null;
  return round1(100 * (1 - (current.idle - previous.idle) / total));
}

export function ramPercent(total: number, free: number): number | null {
  return total > 0 ? round1((100 * (total - free)) / total) : null;
}

/** Porcentaje usado de un volumen a partir de bloques totales y libres para usuarios. */
export function usedPercent(blocks: number, availableBlocks: number): number | null {
  return blocks > 0 ? round1((100 * (blocks - availableBlocks)) / blocks) : null;
}

const round1 = (value: number) => Math.round(Math.min(100, Math.max(0, value)) * 10) / 10;

export interface SamplerSources {
  drives: () => Promise<string[]>;
  temperature: () => Promise<number | null>;
}

/**
 * Muestras de CPU, RAM, disco y temperatura. Los discos y la temperatura requieren WMI (PowerShell,
 * lento), así que se consultan pocas veces y se guardan: cada minuto solo se lee Node (rápido y sin procesos).
 */
export class SystemSampler {
  private previousCpu = readCpuTimes();
  private drives: string[] = [];
  private drivesAt = 0;
  private temperature: number | null = null;
  private temperatureAt = 0;

  constructor(
    private readonly sources: SamplerSources,
    private readonly now: () => number = Date.now,
    private readonly log: (message: string, error: unknown) => void = () => undefined,
  ) {}

  async sample(): Promise<MetricasDatos> {
    const currentCpu = readCpuTimes();
    const cpu = cpuPercent(this.previousCpu, currentCpu);
    this.previousCpu = currentCpu;

    await this.refreshSlowSources();

    return {
      cpu,
      ram: ramPercent(totalmem(), freemem()),
      disco: await this.fullestDisk(),
      temperatura: this.temperature,
    };
  }

  private async refreshSlowSources(): Promise<void> {
    const now = this.now();

    if (now - this.drivesAt > 60 * 60_000) {
      this.drivesAt = now;
      try {
        this.drives = await this.sources.drives();
      } catch (error) {
        this.log('No se pudo listar los discos', error);
      }
    }

    if (now - this.temperatureAt > 5 * 60_000) {
      this.temperatureAt = now;
      try {
        this.temperature = await this.sources.temperature();
      } catch (error) {
        this.log('No se pudo leer la temperatura', error);
        this.temperature = null;
      }
    }
  }

  /** Porcentaje del disco más lleno (así lo guarda la columna metricas.disco). */
  private async fullestDisk(): Promise<number | null> {
    const readings = await Promise.all(
      this.drives.map(async (drive) => {
        try {
          const stats = await statfs(`${drive}\\`);
          return usedPercent(stats.blocks, stats.bavail);
        } catch {
          return null; // unidad desmontada o sin permiso: se omite sin afectar a las demás
        }
      }),
    );
    const valid = readings.filter((value): value is number => value !== null);
    return valid.length > 0 ? Math.max(...valid) : null;
  }
}

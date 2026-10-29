import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { EventosDatos } from '@monitoreoti/shared';
import { runPowerShell } from '../powershell';
import { eventsScript, parseEvents, type EventBookmark } from './health';

/** La primera vez se revisan las últimas 24 h; después, solo lo nuevo desde el último envío. */
const FIRST_LOOKBACK_MS = 24 * 60 * 60_000;
/** Margen hacia atrás en cada consulta: un evento escrito en el mismo segundo de la anterior no se pierde. */
const OVERLAP_MS = 60_000;

/**
 * Errores del Visor de eventos. Guarda en la carpeta de datos hasta qué registro envió, para que un reinicio
 * del servicio no repita eventos (y el servidor tampoco recibe duplicados).
 */
export class EventsCollector {
  private readonly file: string;

  constructor(
    dataDir: string,
    private readonly run: (script: string) => Promise<string> = (script) =>
      runPowerShell(script, { timeoutMs: 60_000 }),
    private readonly now: () => Date = () => new Date(),
  ) {
    this.file = join(dataDir, 'eventos.json');
  }

  async collect(): Promise<EventosDatos> {
    const bookmark = this.load();
    const since = new Date(new Date(bookmark.desde).getTime() - OVERLAP_MS);
    const output = await this.run(eventsScript(since.toISOString()));
    const { datos, bookmark: next } = parseEvents(output, bookmark, this.now());
    this.save(next);
    return datos;
  }

  private load(): EventBookmark {
    try {
      const value = JSON.parse(readFileSync(this.file, 'utf8')) as EventBookmark;
      if (typeof value.desde === 'string' && !Number.isNaN(Date.parse(value.desde))) return value;
    } catch {
      // Primera ejecución o archivo dañado.
    }
    return { desde: new Date(this.now().getTime() - FIRST_LOOKBACK_MS).toISOString() };
  }

  private save(bookmark: EventBookmark): void {
    try {
      writeFileSync(this.file, JSON.stringify(bookmark));
    } catch {
      // Sin poder guardar, en el próximo ciclo se repite la ventana; el servidor ignora los duplicados.
    }
  }
}

import { readFileSync } from 'node:fs';
import { join } from 'node:path';

export const DEFAULT_SERVER_URL = 'http://localhost:5126';

/** Solo http(s) y sin ruta: es el origen que se permite en la CSP. */
export function normalizeServerUrl(value: unknown): string | null {
  if (typeof value !== 'string' || value.length > 300) return null;
  try {
    const url = new URL(value.trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    return url.origin;
  } catch {
    return null;
  }
}

/**
 * Servidor del panel: la variable MONITOREOTI_SERVER_URL (desarrollo) o, en la app instalada, el panel.json que
 * el instalador lleva junto a los recursos ({"servidor": "https://…"}, se escribe al generar el instalador).
 */
export function resolveServerUrl(env: NodeJS.ProcessEnv, resourcesPath: string | null): string {
  const fromEnv = normalizeServerUrl(env['MONITOREOTI_SERVER_URL']);
  if (fromEnv) return fromEnv;
  if (resourcesPath) {
    try {
      const config = JSON.parse(readFileSync(join(resourcesPath, 'panel.json'), 'utf8')) as { servidor?: unknown };
      const fromFile = normalizeServerUrl(config.servidor);
      if (fromFile) return fromFile;
    } catch {
      // Sin panel.json: el servidor local.
    }
  }
  return DEFAULT_SERVER_URL;
}

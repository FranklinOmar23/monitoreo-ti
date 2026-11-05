import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join } from 'node:path';

/**
 * Configuración del modo servicio. En producción la escribe el instalador en config.json (dentro de la
 * carpeta de datos); en desarrollo puede venir de variables de entorno, que tienen prioridad.
 */
export interface ServiceConfig {
  serverUrl: URL;
  heartbeatSeconds: number;
  /** Token de registro de la sucursal; solo hace falta hasta que el agente obtiene su credencial. */
  enrollToken: string | undefined;
  /** Carpeta donde el agente guarda su credencial cifrada y su cola local. */
  dataDir: string;
  agentVersion: string;
}

const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1', '::1', '[::1]']);

export const AGENT_VERSION = '0.3.12';

/** Lo que deja el instalador en config.json. */
export interface ConfigFile {
  serverUrl?: string;
  enrollToken?: string;
}

export const CONFIG_FILE_NAME = 'config.json';

/** Lee config.json de la carpeta de datos; si no existe o está dañado, devuelve un objeto vacío. */
export function readConfigFile(dataDir: string): ConfigFile {
  try {
    const parsed: unknown = JSON.parse(readFileSync(join(dataDir, CONFIG_FILE_NAME), 'utf8'));
    if (typeof parsed !== 'object' || parsed === null) return {};
    const value = parsed as Record<string, unknown>;
    return {
      serverUrl: typeof value['serverUrl'] === 'string' ? value['serverUrl'] : undefined,
      enrollToken: typeof value['enrollToken'] === 'string' ? value['enrollToken'] : undefined,
    };
  } catch {
    return {};
  }
}

/**
 * Quita el token de registro de config.json una vez obtenida la credencial: el token permite registrar
 * equipos en la sucursal y no debe quedar guardado en disco más de lo necesario.
 */
export function removeEnrollToken(dataDir: string): void {
  const path = join(dataDir, CONFIG_FILE_NAME);
  if (!existsSync(path)) return;
  try {
    const value = JSON.parse(readFileSync(path, 'utf8')) as Record<string, unknown>;
    if (!('enrollToken' in value)) return;
    delete value['enrollToken'];
    writeFileSync(path, JSON.stringify(value, null, 2));
  } catch {
    // Archivo dañado o sin permiso: no impide que el agente funcione.
  }
}

export function loadServiceConfig(
  env: Record<string, string | undefined>,
  readFile: (dataDir: string) => ConfigFile = readConfigFile,
): ServiceConfig {
  const dataDir = resolveDataDir(env);
  const file = readFile(dataDir);
  const rawUrl = env['MONITOREOTI_SERVER_URL'] ?? file.serverUrl ?? 'http://localhost:5126';

  let serverUrl: URL;
  try {
    serverUrl = new URL(rawUrl);
  } catch {
    throw new Error(`MONITOREOTI_SERVER_URL no es una URL válida: ${rawUrl}`);
  }

  // El agente solo habla con el servidor por TLS; HTTP se acepta únicamente en el propio equipo (desarrollo).
  if (serverUrl.protocol !== 'https:' && !(serverUrl.protocol === 'http:' && LOCAL_HOSTS.has(serverUrl.hostname))) {
    throw new Error('El servidor debe usar HTTPS (solo se permite HTTP con localhost en desarrollo).');
  }

  const heartbeatSeconds = Number(env['MONITOREOTI_HEARTBEAT_SECONDS'] ?? 30);
  if (!Number.isInteger(heartbeatSeconds) || heartbeatSeconds < 5 || heartbeatSeconds > 300) {
    throw new Error('MONITOREOTI_HEARTBEAT_SECONDS debe ser un entero entre 5 y 300.');
  }

  const enrollToken = env['MONITOREOTI_ENROLL_TOKEN']?.trim() || file.enrollToken?.trim() || undefined;

  return { serverUrl, heartbeatSeconds, enrollToken, dataDir, agentVersion: AGENT_VERSION };
}

/** %ProgramData%\MonitoreoTI\Agent en producción (el instalador restringe la carpeta a SYSTEM y administradores). */
function resolveDataDir(env: Record<string, string | undefined>): string {
  const explicit = env['MONITOREOTI_DATA_DIR']?.trim();
  if (explicit) return explicit;

  const programData = env['ProgramData'];
  return programData ? join(programData, 'MonitoreoTI', 'Agent') : join(homedir(), '.monitoreoti-agent');
}

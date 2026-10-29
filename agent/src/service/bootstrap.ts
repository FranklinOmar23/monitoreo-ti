import type { CredentialStore, StoredCredential } from './credential-store';
import { EnrollmentRejectedError } from './enroll';
import type { Log } from './runtime';

export interface BootstrapOptions {
  serverOrigin: string;
  enrollToken: string | undefined;
  store: Pick<CredentialStore, 'load' | 'save'>;
  enroll: (enrollToken: string) => Promise<StoredCredential>;
  log: Log;
  /** Inyectable para pruebas. */
  sleep?: (ms: number) => Promise<void>;
  isStopped?: () => boolean;
}

const RETRY_MIN_MS = 5_000;
const RETRY_MAX_MS = 5 * 60_000;
const NO_TOKEN_WAIT_MS = 60_000;
const REJECTED_WAIT_MS = 5 * 60_000;

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * Devuelve la credencial del agente: la guardada si es de este servidor, o se registra con el token de la sucursal.
 * No termina el proceso si algo falta (un servicio que muere entra en bucle de reinicios): espera y reintenta,
 * porque el token puede llegar después (el instalador lo escribe) o el servidor puede estar caído.
 * Devuelve null solo si se pidió detener el servicio.
 */
export async function ensureCredential(options: BootstrapOptions): Promise<StoredCredential | null> {
  const sleep = options.sleep ?? defaultSleep;
  const stopped = options.isStopped ?? (() => false);
  let failures = 0;

  while (!stopped()) {
    const stored = await options.store.load();
    if (stored && stored.serverOrigin === options.serverOrigin) return stored;
    if (stored) {
      options.log('info', 'La credencial guardada es de otro servidor; se registra de nuevo', {
        stored: stored.serverOrigin,
      });
    }

    if (!options.enrollToken) {
      options.log('error', 'No hay credencial ni token de registro (MONITOREOTI_ENROLL_TOKEN); se espera');
      await sleep(NO_TOKEN_WAIT_MS);
      continue;
    }

    try {
      const credential = await options.enroll(options.enrollToken);
      await options.store.save(credential);
      options.log('info', 'Equipo registrado en el servidor', {
        machineId: credential.machineId,
        agentId: credential.agentId,
      });
      return credential;
    } catch (error) {
      if (error instanceof EnrollmentRejectedError) {
        options.log('error', error.message);
        await sleep(REJECTED_WAIT_MS);
      } else {
        const wait = Math.min(RETRY_MIN_MS * 2 ** failures++, RETRY_MAX_MS);
        options.log('error', 'No se pudo registrar; se reintenta', { error: String(error), inMs: wait });
        await sleep(wait);
      }
    }
  }
  return null;
}

import type { StoredCredential } from './credential-store';
import type { MachineIdentity } from './machine-info';

/** El servidor rechazó el token (inexistente, vencido o agotado): reintentar no sirve. */
export class EnrollmentRejectedError extends Error {
  constructor() {
    super('El servidor rechazó el token de registro (inválido, vencido o agotado).');
  }
}

type Fetch = typeof fetch;

export interface EnrollOptions {
  serverUrl: URL;
  enrollToken: string;
  agentVersion: string;
  identity: MachineIdentity;
  machineType?: 'computadora' | 'laptop' | 'servidor';
  fetchImpl?: Fetch;
  timeoutMs?: number;
}

/** Registra el equipo con el token de la sucursal y devuelve la credencial permanente. */
export async function enrollAgent(options: EnrollOptions): Promise<StoredCredential> {
  const fetchImpl = options.fetchImpl ?? fetch;
  const response = await fetchImpl(new URL('/agent/register', options.serverUrl), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    signal: AbortSignal.timeout(options.timeoutMs ?? 30_000),
    body: JSON.stringify({
      enrollToken: options.enrollToken,
      hostname: options.identity.hostname,
      macAddress: options.identity.macAddress,
      ipAddress: options.identity.ipAddress,
      operatingSystem: options.identity.operatingSystem,
      machineType: options.machineType ?? 'computadora',
      agentVersion: options.agentVersion,
    }),
  });

  if (response.status === 401) throw new EnrollmentRejectedError();
  if (!response.ok) throw new Error(`El registro falló con HTTP ${response.status}.`);

  const body: unknown = await response.json();
  if (!isEnrollResponse(body)) throw new Error('Respuesta de registro inesperada del servidor.');

  return {
    serverOrigin: options.serverUrl.origin,
    credential: body.credential,
    machineId: body.machineId,
    agentId: body.agentId,
    companyPublicKey: body.companyPublicKey ?? null,
  };
}

interface EnrollResponse {
  credential: string;
  machineId: number;
  agentId: number;
  companyPublicKey?: string | null;
}

function isEnrollResponse(value: unknown): value is EnrollResponse {
  if (typeof value !== 'object' || value === null) return false;
  const v = value as Record<string, unknown>;
  return (
    typeof v['credential'] === 'string' &&
    typeof v['machineId'] === 'number' &&
    typeof v['agentId'] === 'number' &&
    (v['companyPublicKey'] === undefined || v['companyPublicKey'] === null || typeof v['companyPublicKey'] === 'string')
  );
}

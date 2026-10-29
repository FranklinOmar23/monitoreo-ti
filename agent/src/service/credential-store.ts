import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { runPowerShell } from './powershell';

/** Lo que el agente conserva tras registrarse. */
export interface StoredCredential {
  /** Origen del servidor al que pertenece la credencial. */
  serverOrigin: string;
  credential: string;
  machineId: number;
  agentId: number;
  /** Llave pública ECDSA de la empresa (Base64); null si la empresa aún no tiene llave. */
  companyPublicKey: string | null;
}

/** Cifra y descifra bytes. En Windows usa DPAPI; las pruebas inyectan uno propio. */
export interface Protector {
  protect(data: Buffer): Promise<Buffer>;
  unprotect(data: Buffer): Promise<Buffer>;
}

const DPAPI_SCRIPT = (method: 'Protect' | 'Unprotect') => `
Add-Type -AssemblyName System.Security
$bytes = [Convert]::FromBase64String([Console]::In.ReadToEnd().Trim())
$result = [System.Security.Cryptography.ProtectedData]::${method}($bytes, $null, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
[Console]::Out.Write([Convert]::ToBase64String($result))
`;

/**
 * DPAPI con ámbito de máquina: el archivo solo se descifra en este equipo. La carpeta de datos
 * debe limitarse a SYSTEM y administradores (lo hace el instalador), porque el ámbito de máquina
 * permite descifrar a cualquier proceso local que pueda leer el archivo.
 */
export const dpapiProtector: Protector = {
  async protect(data) {
    const output = await runPowerShell(DPAPI_SCRIPT('Protect'), { stdin: data.toString('base64') });
    return Buffer.from(output.trim(), 'base64');
  },
  async unprotect(data) {
    const output = await runPowerShell(DPAPI_SCRIPT('Unprotect'), { stdin: data.toString('base64') });
    return Buffer.from(output.trim(), 'base64');
  },
};

export class CredentialStore {
  private readonly file: string;

  constructor(
    dataDir: string,
    private readonly protector: Protector,
  ) {
    this.file = join(dataDir, 'credential.bin');
  }

  /** null si no hay credencial guardada o no se puede descifrar (otro equipo, archivo dañado). */
  async load(): Promise<StoredCredential | null> {
    let encrypted: Buffer;
    try {
      encrypted = await readFile(this.file);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return null;
      throw error;
    }

    try {
      const parsed: unknown = JSON.parse((await this.protector.unprotect(encrypted)).toString('utf8'));
      return isStoredCredential(parsed) ? parsed : null;
    } catch {
      return null;
    }
  }

  async save(value: StoredCredential): Promise<void> {
    await mkdir(dirname(this.file), { recursive: true });
    const encrypted = await this.protector.protect(Buffer.from(JSON.stringify(value), 'utf8'));
    // Escritura atómica: un corte de luz no deja una credencial a medias.
    const temp = `${this.file}.tmp`;
    await writeFile(temp, encrypted);
    await rename(temp, this.file);
  }
}

function isStoredCredential(value: unknown): value is StoredCredential {
  if (typeof value !== 'object' || value === null) return false;
  const v = value as Record<string, unknown>;
  return (
    typeof v['serverOrigin'] === 'string' &&
    typeof v['credential'] === 'string' &&
    typeof v['machineId'] === 'number' &&
    typeof v['agentId'] === 'number' &&
    (v['companyPublicKey'] === null || typeof v['companyPublicKey'] === 'string')
  );
}

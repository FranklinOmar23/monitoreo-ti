import { spawn } from 'node:child_process';
import { createHash, createPublicKey, verify } from 'node:crypto';
import { mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { createEnvelope, type Envelope } from '@monitoreoti/shared';

/** "ejecutar_comando" tal como lo manda el servidor. */
export interface CommandMessage {
  comando_id: number;
  maquina_id: number;
  tipo: string;
  lenguaje: string;
  contenido: string;
  timeout_s: number;
  vence: string;
  firma: string;
}

export interface RunOutcome {
  exitCode: number | null;
  output: string;
  timedOut: boolean;
}

export type CommandRunner = (language: string, content: string, timeoutSeconds: number) => Promise<RunOutcome>;

/** Ids de comandos ya aceptados: un reenvío del servidor (o alguien que repite un mensaje) no se ejecuta dos veces. */
export interface SeenCommands {
  has(id: number): boolean;
  add(id: number): void;
}

export interface ExecutorOptions {
  machineId: number;
  /** Llave pública de la empresa (SubjectPublicKeyInfo en Base64); null si aún no la tiene. */
  publicKey: () => string | null;
  seen: SeenCommands;
  run: CommandRunner;
  submit: (envelope: Envelope) => void;
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void;
  now?: () => Date;
}

/** Salida máxima que se devuelve (lo demás se recorta). */
export const MAX_OUTPUT_BYTES = 1024 * 1024;

/**
 * Texto que firmó el servidor (espejo de CommandSigning.CommandPayload en el servidor): incluye el equipo y el
 * vencimiento, y el contenido por su SHA-256.
 */
export function signedPayload(m: CommandMessage): string {
  const hash = createHash('sha256').update(m.contenido, 'utf8').digest('hex');
  return ['monitoreoti/comando/v1', m.comando_id, m.maquina_id, m.tipo, m.lenguaje, m.timeout_s, m.vence, hash].join(
    '\n',
  );
}

/** ECDSA P-256 / SHA-256 con la firma en formato IEEE P1363 (r|s), como la produce el servidor. */
export function verifySignature(publicKeyBase64: string, payload: string, signatureBase64: string): boolean {
  try {
    const key = createPublicKey({ key: Buffer.from(publicKeyBase64, 'base64'), format: 'der', type: 'spki' });
    return verify(
      'sha256',
      Buffer.from(payload, 'utf8'),
      { key, dsaEncoding: 'ieee-p1363' },
      Buffer.from(signatureBase64, 'base64'),
    );
  } catch {
    return false;
  }
}

export function parseCommand(value: unknown): CommandMessage | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  const ok =
    Number.isInteger(v['comando_id']) &&
    Number.isInteger(v['maquina_id']) &&
    typeof v['tipo'] === 'string' &&
    typeof v['lenguaje'] === 'string' &&
    typeof v['contenido'] === 'string' &&
    Number.isInteger(v['timeout_s']) &&
    typeof v['vence'] === 'string' &&
    typeof v['firma'] === 'string';
  return ok ? (v as unknown as CommandMessage) : null;
}

/**
 * Ejecutor (sección 8.1): ejecuta solo lo firmado con la llave de su empresa, para este equipo y antes de vencer.
 * Lo demás se rechaza y se informa al servidor (que lo deja en auditoría). Nunca escribe el contenido en el log.
 */
export class CommandExecutor {
  private readonly now: () => Date;

  constructor(private readonly options: ExecutorOptions) {
    this.now = options.now ?? (() => new Date());
  }

  async handle(datos: unknown): Promise<void> {
    const command = parseCommand(datos);
    if (!command) {
      this.options.log('error', 'Comando con formato inválido; se descarta');
      return;
    }

    const reason = this.rejectionReason(command);
    if (reason === 'repetido') {
      this.options.log('info', 'Comando ya recibido; se ignora', { comandoId: command.comando_id });
      return;
    }
    if (reason) {
      this.options.log('error', 'Comando rechazado', { comandoId: command.comando_id, motivo: reason });
      this.report(command.comando_id, { estado: 'rechazado', error: reason });
      return;
    }

    // Se marca antes de ejecutar: si el servicio se reinicia a mitad, no lo vuelve a correr.
    this.options.seen.add(command.comando_id);
    this.report(command.comando_id, { estado: 'ejecutando' });
    this.options.log('info', 'Ejecutando comando', { comandoId: command.comando_id, lenguaje: command.lenguaje });

    try {
      const outcome = await this.options.run(command.lenguaje, command.contenido, command.timeout_s);
      const failed = outcome.timedOut || outcome.exitCode !== 0;
      this.report(command.comando_id, {
        estado: failed ? 'error' : 'completado',
        codigoSalida: outcome.exitCode,
        salida: outcome.output,
        error: outcome.timedOut ? `Se detuvo al pasar el tiempo límite (${command.timeout_s} s).` : undefined,
      });
    } catch (error) {
      this.report(command.comando_id, {
        estado: 'error',
        error: error instanceof Error ? error.message : String(error),
      });
    }
  }

  private rejectionReason(command: CommandMessage): string | null {
    if (this.options.seen.has(command.comando_id)) return 'repetido';
    if (command.maquina_id !== this.options.machineId) return 'el comando es para otro equipo';
    const key = this.options.publicKey();
    if (!key) return 'el agente no tiene la llave de la empresa';
    if (!verifySignature(key, signedPayload(command), command.firma)) return 'firma no válida';
    const expires = Date.parse(command.vence);
    if (Number.isNaN(expires) || expires <= this.now().getTime()) return 'vencido';
    return null;
  }

  private report(id: number, data: Record<string, unknown>): void {
    this.options.submit(createEnvelope('resultado_comando', { comandoId: id, ...data }));
  }
}

/** Lista de ids aceptados en un archivo de la carpeta de datos (solo los últimos 500). */
export class FileSeenCommands implements SeenCommands {
  private readonly file: string;
  private ids: number[];

  constructor(dataDir: string) {
    this.file = join(dataDir, 'comandos-recibidos.json');
    try {
      const parsed: unknown = JSON.parse(readFileSync(this.file, 'utf8'));
      this.ids = Array.isArray(parsed) ? parsed.filter((x): x is number => Number.isInteger(x)) : [];
    } catch {
      this.ids = [];
    }
  }

  has(id: number): boolean {
    return this.ids.includes(id);
  }

  add(id: number): void {
    this.ids = [...this.ids, id].slice(-500);
    const temp = `${this.file}.tmp`;
    writeFileSync(temp, JSON.stringify(this.ids));
    renameSync(temp, this.file);
  }
}

const SYSTEM32 = join(process.env['SystemRoot'] ?? 'C:\\Windows', 'System32');

/**
 * Ejecuta el contenido como archivo temporal (así PowerShell y CMD aceptan bloques de varias líneas) con salida en
 * UTF-8, tiempo límite y salida recortada a 1 MB. El archivo se borra al terminar.
 */
export function createRunner(tempDir: string): CommandRunner {
  return (language, content, timeoutSeconds) => {
    mkdirSync(tempDir, { recursive: true });
    const base = join(tempDir, `cmd-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`);
    let file: string;
    let exe: string;
    let args: string[];
    switch (language) {
      case 'powershell':
        file = `${base}.ps1`;
        // Con BOM: PowerShell 5.1 lee como ANSI un .ps1 sin BOM y daña las tildes.
        writeFileSync(file, `\uFEFF[Console]::OutputEncoding = [Text.Encoding]::UTF8\n${content}`, 'utf8');
        exe = join(SYSTEM32, 'WindowsPowerShell', 'v1.0', 'powershell.exe');
        args = ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', file];
        break;
      case 'cmd':
        file = `${base}.cmd`;
        writeFileSync(file, `@echo off\r\nchcp 65001 >nul\r\n${content.replace(/\r?\n/g, '\r\n')}`, 'utf8');
        exe = join(SYSTEM32, 'cmd.exe');
        args = ['/d', '/c', file];
        break;
      case 'python':
        file = `${base}.py`;
        writeFileSync(file, content, 'utf8');
        exe = 'python.exe';
        args = [file];
        break;
      default:
        return Promise.reject(new Error(`El lenguaje ${language} no está disponible en este equipo.`));
    }

    return runProcess(exe, args, timeoutSeconds * 1000).finally(() => rmSync(file, { force: true }));
  };
}

function runProcess(exe: string, args: string[], timeoutMs: number): Promise<RunOutcome> {
  return new Promise((resolve, reject) => {
    const child = spawn(exe, args, {
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'pipe'],
      env: { ...process.env, PYTHONIOENCODING: 'utf-8', ELECTRON_RUN_AS_NODE: undefined },
    });
    const chunks: Buffer[] = [];
    let size = 0;
    let truncated = false;
    let timedOut = false;
    const collect = (chunk: Buffer) => {
      if (size >= MAX_OUTPUT_BYTES) {
        truncated = true;
        return;
      }
      chunks.push(chunk.subarray(0, MAX_OUTPUT_BYTES - size));
      size += Math.min(chunk.length, MAX_OUTPUT_BYTES - size);
    };
    child.stdout.on('data', collect);
    child.stderr.on('data', collect);

    const timer = setTimeout(() => {
      timedOut = true;
      // Se detiene el árbol completo: un script puede haber lanzado otros procesos.
      if (child.pid)
        spawn(join(SYSTEM32, 'taskkill.exe'), ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true });
      else child.kill();
    }, timeoutMs);

    child.on('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      const output = Buffer.concat(chunks).toString('utf8') + (truncated ? '\n[salida recortada a 1 MB]' : '');
      resolve({ exitCode: code, output, timedOut });
    });
  });
}

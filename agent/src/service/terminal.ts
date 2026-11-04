import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync } from 'node:fs';
import type { SessionChannel, SessionHandler, SessionHandlerFactory } from './remote-sessions';

/**
 * Terminal remota (sección 10.2) sobre el relay:
 * - del panel: texto {"tipo":"iniciar","shell":"powershell"|"cmd","columnas","filas"} y {"tipo":"tamano",...};
 *   binario = lo que escribe el técnico.
 * - al panel: texto {"tipo":"listo","shell"}, {"tipo":"fin","codigo"} o {"tipo":"error","mensaje"}; binario = salida.
 * La consola la abre el anfitrión C# ("MonitoreoTI.AgentHost.exe pty", ConPTY) como SYSTEM (decisión 52).
 */

export const INPUT_FRAME = 1;
export const RESIZE_FRAME = 2;

/** Mensaje para el anfitrión: [tipo:1][largo:4 LE][datos]. */
export function hostFrame(type: number, payload: Uint8Array): Buffer {
  const header = Buffer.alloc(5);
  header[0] = type;
  header.writeInt32LE(payload.length, 1);
  return Buffer.concat([header, payload]);
}

export function resizePayload(cols: number, rows: number): Buffer {
  const payload = Buffer.alloc(4);
  payload.writeInt16LE(clamp(cols, 1, 999), 0);
  payload.writeInt16LE(clamp(rows, 1, 499), 2);
  return payload;
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, Math.round(Number.isFinite(value) ? value : min)));
}

export type PtySpawner = (args: string[]) => ChildProcessWithoutNullStreams;

export function terminalHandlerFactory(
  hostExe: string | undefined,
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void,
  spawnPty: PtySpawner = (args) =>
    spawn(hostExe!, ['pty', ...args], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }),
): SessionHandlerFactory {
  return (channel) => new TerminalSession(channel, hostExe, spawnPty, log);
}

class TerminalSession implements SessionHandler {
  private child: ChildProcessWithoutNullStreams | null = null;
  private disposed = false;

  constructor(
    private readonly channel: SessionChannel,
    private readonly hostExe: string | undefined,
    private readonly spawnPty: PtySpawner,
    private readonly log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void,
  ) {}

  onText(text: string): void {
    let message: Record<string, unknown>;
    try {
      message = JSON.parse(text) as Record<string, unknown>;
    } catch {
      return;
    }
    if (message['tipo'] === 'iniciar' && !this.child) this.start(message);
    else if (message['tipo'] === 'tamano' && this.child)
      this.write(hostFrame(RESIZE_FRAME, resizePayload(Number(message['columnas']), Number(message['filas']))));
  }

  onBinary(data: Uint8Array): void {
    if (this.child) this.write(hostFrame(INPUT_FRAME, data));
  }

  dispose(): void {
    this.disposed = true;
    const child = this.child;
    this.child = null;
    if (!child) return;
    // Cerrar la entrada le indica al anfitrión que termine el shell; si no lo hace, se fuerza.
    child.stdin.end();
    setTimeout(() => {
      if (child.exitCode === null) child.kill();
    }, 2000).unref();
  }

  private start(message: Record<string, unknown>): void {
    if (!this.hostExe || !existsSync(this.hostExe)) {
      this.fail('La terminal no está disponible en este equipo (falta el anfitrión del servicio).');
      return;
    }
    const shell = message['shell'] === 'cmd' ? 'cmd' : 'powershell';
    const cols = clamp(Number(message['columnas']), 20, 999);
    const rows = clamp(Number(message['filas']), 5, 499);

    let child: ChildProcessWithoutNullStreams;
    try {
      child = this.spawnPty(['--shell', shell, '--cols', String(cols), '--rows', String(rows)]);
    } catch (error) {
      this.fail(`No se pudo abrir la terminal: ${String(error)}`);
      return;
    }
    this.child = child;
    child.stdout.on('data', (chunk: Buffer) => this.channel.sendBinary(chunk));
    child.stderr
      .setEncoding('utf8')
      .on('data', (text: string) => this.log('error', 'Terminal', { detalle: text.trim() }));
    child.stdin.on('error', () => {
      // El shell ya terminó.
    });
    child.on('error', (error) => this.fail(`No se pudo abrir la terminal: ${error.message}`));
    child.on('close', (code) => {
      if (this.disposed) return;
      this.child = null;
      this.channel.sendText(JSON.stringify({ tipo: 'fin', codigo: code }));
      this.channel.close();
    });
    this.channel.sendText(JSON.stringify({ tipo: 'listo', shell }));
    this.log('info', 'Terminal abierta', { shell });
  }

  private write(frame: Buffer): void {
    try {
      this.child?.stdin.write(frame);
    } catch {
      // Se cerró: el evento close termina la sesión.
    }
  }

  private fail(text: string): void {
    this.channel.sendText(JSON.stringify({ tipo: 'error', mensaje: text }));
    this.channel.close();
  }
}

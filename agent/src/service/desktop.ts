import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { existsSync } from 'node:fs';
import { filesHandlerFactory, type FileActivity } from './files';
import type { SessionChannel, SessionHandler, SessionHandlerFactory } from './remote-sessions';

/**
 * Escritorio remoto (Fase 7) sobre el relay. La pantalla y el control los atiende el asistente de sesión, que corre en
 * la sesión del usuario: lo abre "MonitoreoTI.AgentHost.exe escritorio" (el servicio no puede ver ese escritorio).
 * Entre el agente y el lanzador van mensajes [tipo:1][largo:4 LE][datos] (1 = texto, 2 = binario) que aquí se
 * convierten en marcos del relay tal cual: el protocolo del visor lo define DesktopHelper (anfitrión C#).
 */

export const TEXT_FRAME = 1;
export const BINARY_FRAME = 2;

/**
 * Primer byte de los mensajes binarios del escritorio hacia el visor: 1 = pantalla JPEG y 3 = pantalla en video (del
 * asistente), 2 = parte de un archivo (descargas del explorador dentro del escritorio). Del visor, todo binario es una parte de archivo con el 2.
 */
export const FILE_PART = 2;

/** Acciones del visor que también quedan en la auditoría del servidor (además de pasar al asistente). */
const AUDITED: Record<string, (message: Record<string, unknown>) => string | null> = {
  accion: (m) =>
    ['cambiar_usuario', 'cerrar_sesion', 'reiniciar'].includes(String(m['accion'])) ? String(m['accion']) : null,
  bloquear_entrada: (m) => (m['activo'] === true ? 'bloquear_entrada' : 'desbloquear_entrada'),
  pantalla_negra: (m) => (m['activo'] === true ? 'pantalla_negra' : 'quitar_pantalla_negra'),
};

export function encodeFrame(type: number, payload: Uint8Array): Buffer {
  const header = Buffer.alloc(5);
  header[0] = type;
  header.writeInt32LE(payload.length, 1);
  return Buffer.concat([header, payload]);
}

/** Separa los mensajes del flujo del lanzador (pueden llegar partidos o varios juntos). */
export class FrameReader {
  private buffer: Buffer = Buffer.alloc(0);

  push(chunk: Buffer, onFrame: (type: number, data: Buffer) => void): void {
    this.buffer = this.buffer.length === 0 ? chunk : Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 5) {
      const length = this.buffer.readInt32LE(1);
      if (this.buffer.length < 5 + length) return;
      onFrame(this.buffer[0]!, this.buffer.subarray(5, 5 + length));
      this.buffer = this.buffer.subarray(5 + length);
    }
  }
}

export type DesktopSpawner = () => ChildProcessWithoutNullStreams;

export function desktopHandlerFactory(
  hostExe: string | undefined,
  log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void,
  spawnLauncher: DesktopSpawner = () =>
    spawn(hostExe!, ['escritorio'], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }),
  /** Lo que se hizo en la sesión (archivos y acciones del sistema), para la auditoría. */
  report: (sessionId: number, activity: FileActivity | { accion: string }) => void = () => undefined,
): SessionHandlerFactory {
  return (channel) => new DesktopSession(channel, hostExe, spawnLauncher, log, report);
}

class DesktopSession implements SessionHandler {
  private child: ChildProcessWithoutNullStreams | null = null;
  private disposed = false;
  private readonly reader = new FrameReader();
  /** Explorador de archivos dentro del escritorio (solo con remoto.archivos). */
  private readonly files: SessionHandler | null;

  constructor(
    private readonly channel: SessionChannel,
    hostExe: string | undefined,
    spawnLauncher: DesktopSpawner,
    private readonly log: (level: 'info' | 'error', message: string, extra?: Record<string, unknown>) => void,
    private readonly report: (sessionId: number, activity: FileActivity | { accion: string }) => void,
  ) {
    this.files = channel.allowFiles
      ? filesHandlerFactory((sessionId, activity) => report(sessionId, activity))({
          ...channel,
          // Las partes de archivo hacia el visor llevan su marcador (las de pantalla empiezan con 1).
          sendBinary: (data) => channel.sendBinary(Buffer.concat([Buffer.from([FILE_PART]), Buffer.from(data)])),
          close: () => undefined,
        })
      : null;
    if (!hostExe || !existsSync(hostExe)) {
      this.fail('El escritorio remoto no está disponible en este equipo (falta el anfitrión del servicio).');
      return;
    }
    try {
      this.child = spawnLauncher();
    } catch (error) {
      this.fail(`No se pudo abrir el escritorio remoto: ${String(error)}`);
      return;
    }
    const child = this.child;
    child.stdout.on('data', (chunk: Buffer) =>
      this.reader.push(chunk, (type, data) => {
        if (type === TEXT_FRAME) this.channel.sendText(data.toString('utf8'));
        else if (type === BINARY_FRAME) this.channel.sendBinary(data);
      }),
    );
    child.stderr
      .setEncoding('utf8')
      .on('data', (text: string) => log('error', 'Escritorio remoto', { detalle: text.trim() }));
    child.stdin.on('error', () => {
      // El lanzador ya terminó.
    });
    child.on('error', (error) => this.fail(`No se pudo abrir el escritorio remoto: ${error.message}`));
    child.on('close', (code) => {
      this.child = null;
      if (this.disposed) return;
      log('info', 'Escritorio remoto terminado', { codigo: code });
      this.channel.close();
    });
    // Lo primero que recibe el asistente: quién se conecta (para el aviso) y si debe pedir permiso.
    this.write(
      TEXT_FRAME,
      Buffer.from(
        JSON.stringify({
          tipo: 'inicio',
          tecnico: channel.technician,
          pedirPermiso: channel.askConsent,
          soloVer: channel.viewOnly,
        }),
      ),
    );
    log('info', 'Escritorio remoto abierto', { sesionId: channel.sessionId });
  }

  onText(text: string): void {
    let message: Record<string, unknown> | null = null;
    try {
      message = JSON.parse(text) as Record<string, unknown>;
    } catch {
      // No es JSON: se pasa tal cual.
    }
    // Pedidos del explorador de archivos ({op, id}): los atiende el agente, no el asistente.
    if (message && typeof message['op'] === 'string') {
      if (this.files) this.files.onText(text);
      else
        this.channel.sendText(
          JSON.stringify({ id: message['id'], ok: false, error: 'No tiene permiso para transferir archivos.' }),
        );
      return;
    }
    const audited = message ? AUDITED[String(message['tipo'])]?.(message) : null;
    if (audited && !this.channel.viewOnly) this.report(this.channel.sessionId, { accion: audited });
    this.write(TEXT_FRAME, Buffer.from(text, 'utf8'));
  }

  onBinary(data: Uint8Array): void {
    // Del visor, el binario es una parte de un archivo que se sube: [2][id:4][datos].
    if (this.files && data[0] === FILE_PART) this.files.onBinary(data.subarray(1));
  }

  dispose(): void {
    this.disposed = true;
    this.files?.dispose();
    const child = this.child;
    this.child = null;
    if (!child) return;
    // Al cerrarse su entrada, el asistente quita el aviso y termina; si no lo hace, se fuerza.
    child.stdin.end();
    setTimeout(() => {
      if (child.exitCode === null) child.kill();
    }, 5000).unref();
  }

  private write(type: number, payload: Uint8Array): void {
    try {
      this.child?.stdin.write(encodeFrame(type, payload));
    } catch {
      // Se cerró: el evento close termina la sesión.
    }
  }

  private fail(text: string): void {
    this.channel.sendText(JSON.stringify({ tipo: 'error', mensaje: text }));
    this.channel.close();
  }
}

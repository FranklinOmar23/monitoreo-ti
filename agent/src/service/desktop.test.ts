import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { describe, expect, it, vi } from 'vitest';
import { desktopHandlerFactory, encodeFrame, FrameReader } from './desktop';

function fakeChild() {
  const child = new EventEmitter() as unknown as ChildProcessWithoutNullStreams & { written: Buffer[] };
  const stdin = new PassThrough();
  const written: Buffer[] = [];
  stdin.on('data', (chunk: Buffer) => written.push(chunk));
  Object.assign(child, {
    stdin,
    stdout: new PassThrough(),
    stderr: new PassThrough(),
    exitCode: null,
    kill: vi.fn(),
    written,
  });
  return child;
}

function setup(askConsent = false, extra: { viewOnly?: boolean; allowFiles?: boolean } = {}) {
  const child = fakeChild();
  const channel = {
    sessionId: 12,
    technician: 'Luis Peña',
    askConsent,
    viewOnly: false,
    allowFiles: false,
    sendText: vi.fn(),
    sendBinary: vi.fn(),
    close: vi.fn(),
  };
  Object.assign(channel, extra);
  const report = vi.fn();
  const handler = desktopHandlerFactory(process.execPath, vi.fn(), () => child, report)(channel);
  return { child, channel, handler, report };
}

const frames = (buffers: Buffer[]) => {
  const result: [number, string][] = [];
  new FrameReader().push(Buffer.concat(buffers), (type, data) => result.push([type, data.toString()]));
  return result;
};

describe('escritorio remoto en el agente', () => {
  it('le dice al asistente quién se conecta y si debe pedir permiso', async () => {
    const { child } = setup(true);
    await new Promise((r) => setImmediate(r));
    expect(frames(child.written)).toEqual([
      [1, JSON.stringify({ tipo: 'inicio', tecnico: 'Luis Peña', pedirPermiso: true, soloVer: false })],
    ]);
  });

  it('pasa el texto y el binario en ambos sentidos con su tipo', async () => {
    const { child, channel, handler } = setup();
    handler.onText(JSON.stringify({ tipo: 'tecla', codigo: 'KeyA', abajo: true }));
    await new Promise((r) => setImmediate(r));
    expect(frames(child.written)[1]).toEqual([1, JSON.stringify({ tipo: 'tecla', codigo: 'KeyA', abajo: true })]);

    // Del asistente: un mensaje partido en dos pedazos y otro pegado detrás.
    const out = Buffer.concat([
      encodeFrame(1, Buffer.from('{"tipo":"listo"}')),
      encodeFrame(2, Buffer.from([1, 2, 3])),
    ]);
    (child.stdout as PassThrough).write(out.subarray(0, 7));
    (child.stdout as PassThrough).write(out.subarray(7));
    await new Promise((r) => setImmediate(r));
    expect(channel.sendText).toHaveBeenCalledWith('{"tipo":"listo"}');
    expect(channel.sendBinary).toHaveBeenCalledWith(Buffer.from([1, 2, 3]));
  });

  it('cierra la sesión cuando termina el asistente', () => {
    const { child, channel } = setup();
    child.emit('close', 0);
    expect(channel.close).toHaveBeenCalled();
  });

  it('explica que falta el anfitrión', () => {
    const channel = {
      sessionId: 1,
      technician: 'x',
      askConsent: false,
      viewOnly: false,
      allowFiles: false,
      sendText: vi.fn(),
      sendBinary: vi.fn(),
      close: vi.fn(),
    };
    desktopHandlerFactory(undefined, vi.fn())(channel);
    expect(String(channel.sendText.mock.calls[0]![0])).toContain('falta el anfitrión');
  });

  it('le dice al asistente si la sesión es de solo ver', async () => {
    const { child } = setup(false, { viewOnly: true });
    await new Promise((r) => setImmediate(r));
    expect(frames(child.written)[0]![1]).toContain('"soloVer":true');
  });

  it('las acciones del sistema van al asistente y quedan para la auditoría', async () => {
    const { child, handler, report } = setup();
    handler.onText(JSON.stringify({ tipo: 'accion', accion: 'reiniciar' }));
    handler.onText(JSON.stringify({ tipo: 'bloquear_entrada', activo: true }));
    await new Promise((r) => setImmediate(r));

    expect(report).toHaveBeenCalledWith(12, { accion: 'reiniciar' });
    expect(report).toHaveBeenCalledWith(12, { accion: 'bloquear_entrada' });
    expect(frames(child.written).some(([, t]) => t.includes('reiniciar'))).toBe(true);
  });

  it('atiende el explorador de archivos dentro del escritorio solo con permiso', () => {
    const denied = setup();
    denied.handler.onText(JSON.stringify({ op: 'listar', id: 7, ruta: '' }));
    expect(denied.channel.sendText).toHaveBeenCalledWith(
      JSON.stringify({ id: 7, ok: false, error: 'No tiene permiso para transferir archivos.' }),
    );

    const allowed = setup(false, { allowFiles: true });
    allowed.handler.onText(JSON.stringify({ op: 'listar', id: 8, ruta: '' }));
    // No pasa al asistente: lo atiende el agente.
    expect(frames(allowed.child.written).some(([, t]) => t.includes('listar'))).toBe(false);
  });
});

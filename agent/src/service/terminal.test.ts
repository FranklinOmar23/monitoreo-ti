import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { describe, expect, it, vi } from 'vitest';
import { hostFrame, resizePayload, terminalHandlerFactory } from './terminal';

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

function setup() {
  const child = fakeChild();
  const spawnPty = vi.fn(() => child);
  const channel = {
    sessionId: 1,
    technician: 'Ana',
    askConsent: false,
    viewOnly: false,
    allowFiles: false,
    sendText: vi.fn(),
    sendBinary: vi.fn(),
    close: vi.fn(),
  };
  // process.execPath existe: hace las veces del anfitrión en la prueba.
  const handler = terminalHandlerFactory(process.execPath, vi.fn(), spawnPty)(channel);
  return { child, spawnPty, channel, handler };
}

describe('terminal remota en el agente', () => {
  it('abre el shell pedido con su tamaño y pasa la salida al técnico', () => {
    const { child, spawnPty, channel, handler } = setup();
    handler.onText(JSON.stringify({ tipo: 'iniciar', shell: 'cmd', columnas: 100, filas: 30 }));

    expect(spawnPty).toHaveBeenCalledWith(['--shell', 'cmd', '--cols', '100', '--rows', '30']);
    expect(channel.sendText).toHaveBeenCalledWith(JSON.stringify({ tipo: 'listo', shell: 'cmd' }));
    (child.stdout as PassThrough).write(Buffer.from('PS C:/>'));
    expect(channel.sendBinary).toHaveBeenCalledWith(Buffer.from('PS C:/>'));
  });

  it('manda las teclas y el tamaño al anfitrión con su encabezado', async () => {
    const { child, handler } = setup();
    handler.onText(JSON.stringify({ tipo: 'iniciar', shell: 'powershell', columnas: 80, filas: 24 }));
    handler.onBinary(new TextEncoder().encode('dir\r'));
    handler.onText(JSON.stringify({ tipo: 'tamano', columnas: 132, filas: 40 }));
    await new Promise((r) => setImmediate(r));

    expect(Buffer.concat(child.written)).toEqual(
      Buffer.concat([hostFrame(1, Buffer.from('dir\r')), hostFrame(2, resizePayload(132, 40))]),
    );
  });

  it('avisa el fin del shell y cierra la sesión', () => {
    const { child, channel, handler } = setup();
    handler.onText(JSON.stringify({ tipo: 'iniciar', shell: 'cmd', columnas: 80, filas: 24 }));
    child.emit('close', 0);

    expect(channel.sendText).toHaveBeenLastCalledWith(JSON.stringify({ tipo: 'fin', codigo: 0 }));
    expect(channel.close).toHaveBeenCalled();
  });

  it('explica que falta el anfitrión si no está instalado', () => {
    const channel = {
      sessionId: 1,
      technician: 'Ana',
      askConsent: false,
      viewOnly: false,
      allowFiles: false,
      sendText: vi.fn(),
      sendBinary: vi.fn(),
      close: vi.fn(),
    };
    const handler = terminalHandlerFactory(undefined, vi.fn())(channel);
    handler.onText(JSON.stringify({ tipo: 'iniciar', shell: 'cmd', columnas: 80, filas: 24 }));

    expect(String(channel.sendText.mock.calls[0]![0])).toContain('falta el anfitrión');
    expect(channel.close).toHaveBeenCalled();
  });
});

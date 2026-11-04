import { generateKeyPairSync, sign } from 'node:crypto';
import type { Envelope } from '@monitoreoti/shared';
import { describe, expect, it, vi } from 'vitest';
import { CommandExecutor, signedPayload, type CommandMessage, type CommandRunner } from './executor';

const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const publicKeyBase64 = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const other = generateKeyPairSync('ec', { namedCurve: 'P-256' });

function signed(overrides: Partial<CommandMessage> = {}, key = privateKey): CommandMessage {
  const message: CommandMessage = {
    comando_id: 41,
    maquina_id: 7,
    tipo: 'script',
    lenguaje: 'powershell',
    contenido: 'Get-Date',
    timeout_s: 60,
    vence: '2026-10-31T12:00:00Z',
    firma: '',
    ...overrides,
  };
  message.firma = sign('sha256', Buffer.from(signedPayload(message)), { key, dsaEncoding: 'ieee-p1363' }).toString(
    'base64',
  );
  return message;
}

function setup(run: CommandRunner = () => Promise.resolve({ exitCode: 0, output: 'listo', timedOut: false })) {
  const sent: Envelope[] = [];
  const seen = new Set<number>();
  const runner = vi.fn(run);
  const executor = new CommandExecutor({
    machineId: 7,
    publicKey: () => publicKeyBase64,
    seen: { has: (id) => seen.has(id), add: (id) => seen.add(id) },
    run: runner,
    submit: (envelope) => sent.push(envelope),
    log: vi.fn(),
    now: () => new Date('2026-10-31T10:00:00Z'),
  });
  const states = () => sent.map((e) => (e.datos as { estado: string }).estado);
  return { executor, sent, runner, states };
}

describe('ejecutor de comandos firmados', () => {
  it('reconstruye el mismo texto firmado que el servidor (vector de CommandSigningTests)', () => {
    expect(signedPayload(signed({ comando_id: 7, maquina_id: 3 }))).toBe(
      'monitoreoti/comando/v1\n7\n3\nscript\npowershell\n60\n2026-10-31T12:00:00Z\n' +
        'c1d9af5ecced5022dd0e037ef3d6e96a33b43dea902d1c05c894b7b33cb3c798',
    );
  });

  it('ejecuta lo firmado y devuelve la salida', async () => {
    const { executor, sent, runner, states } = setup();
    await executor.handle(signed());

    expect(runner).toHaveBeenCalledWith('powershell', 'Get-Date', 60);
    expect(states()).toEqual(['ejecutando', 'completado']);
    expect(sent[1]!.tipo).toBe('resultado_comando');
    expect(sent[1]!.datos).toMatchObject({ comandoId: 41, codigoSalida: 0, salida: 'listo' });
  });

  it('rechaza un script sin firma válida y no lo ejecuta', async () => {
    const { executor, sent, runner } = setup();
    const tampered = { ...signed(), contenido: 'Remove-Item C:\\ -Recurse' };
    await executor.handle(tampered);
    await executor.handle(signed({ comando_id: 42 }, other.privateKey));

    expect(runner).not.toHaveBeenCalled();
    expect(sent.map((e) => e.datos)).toEqual([
      { comandoId: 41, estado: 'rechazado', error: 'firma no válida' },
      { comandoId: 42, estado: 'rechazado', error: 'firma no válida' },
    ]);
  });

  it('rechaza lo que es para otro equipo o ya venció', async () => {
    const { executor, sent, runner } = setup();
    await executor.handle(signed({ maquina_id: 8 }));
    await executor.handle(signed({ comando_id: 43, vence: '2026-10-31T09:59:59Z' }));

    expect(runner).not.toHaveBeenCalled();
    expect(sent.map((e) => (e.datos as { error: string }).error)).toEqual([
      'el comando es para otro equipo',
      'vencido',
    ]);
  });

  it('no ejecuta dos veces el mismo comando', async () => {
    const { executor, runner, states } = setup();
    await executor.handle(signed());
    await executor.handle(signed());

    expect(runner).toHaveBeenCalledTimes(1);
    expect(states()).toEqual(['ejecutando', 'completado']);
  });

  it('informa como error una salida distinta de cero o el tiempo límite', async () => {
    const { executor, sent } = setup(() => Promise.resolve({ exitCode: null, output: 'parcial', timedOut: true }));
    await executor.handle(signed());

    expect(sent[1]!.datos).toMatchObject({
      estado: 'error',
      salida: 'parcial',
      error: 'Se detuvo al pasar el tiempo límite (60 s).',
    });
  });
});

import type { Envelope } from '@monitoreoti/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ensureCredential } from './bootstrap';
import type { StoredCredential } from './credential-store';
import { EnrollmentRejectedError } from './enroll';
import type { ServerWelcome } from './gateway-client';
import { Outbox } from './outbox';
import { AgentRuntime, type Collectors, type Gateway } from './runtime';

const stored: StoredCredential = {
  serverOrigin: 'http://localhost:5126',
  credential: '1.abc',
  machineId: 7,
  agentId: 3,
  companyPublicKey: null,
};

describe('ensureCredential', () => {
  const base = () => ({
    serverOrigin: 'http://localhost:5126',
    log: vi.fn(),
    sleep: vi.fn<(ms: number) => Promise<void>>(() => Promise.resolve()),
  });

  it('usa la credencial guardada sin volver a registrarse', async () => {
    const enroll = vi.fn();

    const result = await ensureCredential({
      ...base(),
      enrollToken: undefined,
      store: { load: () => Promise.resolve(stored), save: vi.fn() },
      enroll,
    });

    expect(result).toBe(stored);
    expect(enroll).not.toHaveBeenCalled();
  });

  it('se registra con el token y guarda la credencial', async () => {
    const save = vi.fn(() => Promise.resolve());

    const result = await ensureCredential({
      ...base(),
      enrollToken: '1.secreto',
      store: { load: () => Promise.resolve(null), save },
      enroll: () => Promise.resolve(stored),
    });

    expect(result).toBe(stored);
    expect(save).toHaveBeenCalledWith(stored);
  });

  it('descarta la credencial de otro servidor y se registra de nuevo', async () => {
    const other = { ...stored, serverOrigin: 'https://otro.example' };
    const enroll = vi.fn(() => Promise.resolve(stored));

    const result = await ensureCredential({
      ...base(),
      enrollToken: 't',
      store: { load: () => Promise.resolve(other), save: () => Promise.resolve() },
      enroll,
    });

    expect(result).toBe(stored);
    expect(enroll).toHaveBeenCalledOnce();
  });

  it('sin credencial ni token espera y reintenta en vez de terminar', async () => {
    const deps = base();
    let loads = 0;

    const result = await ensureCredential({
      ...deps,
      enrollToken: undefined,
      store: { load: () => Promise.resolve(++loads < 3 ? null : stored), save: vi.fn() },
      enroll: vi.fn(),
    });

    expect(result).toBe(stored);
    expect(deps.sleep).toHaveBeenCalledTimes(2);
  });

  it('reintenta con espera creciente los fallos temporales y espera más tras un rechazo', async () => {
    const deps = base();
    const enroll = vi
      .fn<() => Promise<StoredCredential>>()
      .mockRejectedValueOnce(new Error('sin red'))
      .mockRejectedValueOnce(new Error('sin red'))
      .mockRejectedValueOnce(new EnrollmentRejectedError())
      .mockResolvedValue(stored);

    const result = await ensureCredential({
      ...deps,
      enrollToken: 't',
      store: { load: () => Promise.resolve(null), save: () => Promise.resolve() },
      enroll,
    });

    expect(result).toBe(stored);
    expect(deps.sleep.mock.calls.map(([ms]) => ms)).toEqual([5_000, 10_000, 300_000]);
  });

  it('termina con null si se pide detener el servicio', async () => {
    const result = await ensureCredential({
      ...base(),
      enrollToken: 't',
      store: { load: () => Promise.resolve(null), save: vi.fn() },
      enroll: vi.fn(),
      isStopped: () => true,
    });

    expect(result).toBeNull();
  });
});

describe('AgentRuntime', () => {
  let outbox: Outbox;
  let sent: Envelope[];
  let online: boolean;
  let onReady: (welcome: ServerWelcome) => void;
  let onMessage: (message: Envelope) => void;
  let stopped: boolean;

  const gateway: Gateway = {
    start: () => undefined,
    stop: () => {
      stopped = true;
    },
    request: (envelope) => {
      if (!online) return Promise.reject(new Error('sin conexión'));
      sent.push(envelope);
      return Promise.resolve();
    },
  };

  const collectors: Collectors = {
    metrics: () => Promise.resolve({ cpu: 10, ram: 50, disco: 60, temperatura: null }),
    hardware: () => Promise.resolve({ tipo: 'computadora', componentes: [] }),
    software: () => Promise.resolve({ programas: [] }),
    interfaces: () => Promise.resolve({ interfaces: [] }),
  };

  const flush = () => vi.advanceTimersByTimeAsync(0);

  const createRuntime = (
    override: Partial<Collectors> = {},
    snmp?: ConstructorParameters<typeof AgentRuntime>[0]['snmp'],
  ) =>
    new AgentRuntime({
      outbox,
      collectors: { ...collectors, ...override },
      log: vi.fn(),
      startupDelaysMs: { hardware: 1_000, software: 2_000, interfaces: 3_000 },
      snmp,
      createGateway: (callbacks) => {
        onReady = callbacks.onReady;
        onMessage = callbacks.onMessage;
        return gateway;
      },
    });

  beforeEach(() => {
    vi.useFakeTimers();
    outbox = new Outbox(':memory:');
    sent = [];
    online = false;
    stopped = false;
  });
  afterEach(() => vi.useRealTimers());

  it('recolecta métricas cada minuto aunque no haya conexión y las guarda en la cola', async () => {
    createRuntime().start();

    await vi.advanceTimersByTimeAsync(60_000);
    await vi.advanceTimersByTimeAsync(60_000);

    expect(outbox.peek(10).filter((m) => m.tipo === 'metricas')).toHaveLength(2);
    expect(sent).toHaveLength(0);
  });

  it('al conectar envía lo acumulado en orden y con conexión envía directo', async () => {
    createRuntime().start();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(outbox.count()).toBeGreaterThan(0);

    online = true;
    onReady({ heartbeatSeconds: 30, metricsSeconds: 60, isCollector: false, collector: null });
    await flush();
    expect(outbox.count()).toBe(0);
    expect(sent.map((m) => m.tipo)).toContain('metricas');

    await vi.advanceTimersByTimeAsync(60_000);
    expect(sent.filter((m) => m.tipo === 'metricas')).toHaveLength(2);
  });

  it('envía los inventarios al arrancar, escalonados', async () => {
    online = true;
    createRuntime().start();
    onReady({ heartbeatSeconds: 30, metricsSeconds: 60, isCollector: false, collector: null });

    await vi.advanceTimersByTimeAsync(3_000);

    expect(sent.map((m) => m.tipo)).toEqual(['inventario_hardware', 'inventario_software', 'interfaces_red']);
  });

  it('un recolector que falla no detiene a los demás', async () => {
    online = true;
    createRuntime({ hardware: () => Promise.reject(new Error('WMI caído')) }).start();
    onReady({ heartbeatSeconds: 30, metricsSeconds: 60, isCollector: false, collector: null });

    await vi.advanceTimersByTimeAsync(3_000);

    expect(sent.map((m) => m.tipo)).toEqual(['inventario_software', 'interfaces_red']);
  });

  it('usa el intervalo de métricas que indica el servidor', async () => {
    online = true;
    createRuntime().start();
    onReady({ heartbeatSeconds: 30, metricsSeconds: 10, isCollector: false, collector: null });

    await vi.advanceTimersByTimeAsync(30_000);

    expect(sent.filter((m) => m.tipo === 'metricas')).toHaveLength(3);
  });

  it('se vuelve recolector con la lista del servidor y la cambia con config_actualizada', async () => {
    online = true;
    const query = vi.fn(() => Promise.resolve({ ok: true }));
    const runtime = createRuntime({}, { credential: '1.abc', query, firstDelayMs: 500 });
    runtime.start();
    const device = { id: 4, nombre: 'SW', ip: '10.0.0.2', tipo: 'switch', usuario: 'm', credencial: 'x' };

    onReady({
      heartbeatSeconds: 30,
      metricsSeconds: 60,
      isCollector: true,
      collector: { intervalSeconds: 300, devices: [device] },
    });
    expect(runtime.snmpDevices).toBe(1);
    await vi.advanceTimersByTimeAsync(500);
    // La credencial de prueba no abre: el sondeo se informa como fallido, sin consultar.
    expect(sent.find((m) => m.tipo === 'snmp_resultado')?.datos).toMatchObject({ dispositivoId: 4, ok: false });

    onMessage({
      v: 1,
      tipo: 'config_actualizada',
      id: 'c1',
      ts: new Date().toISOString(),
      datos: { recolector: null },
    });
    expect(runtime.snmpDevices).toBe(0);
    runtime.stop();
  });

  it('al detenerse cancela todo y cierra la conexión', async () => {
    const runtime = createRuntime();
    runtime.start();

    runtime.stop();
    await vi.advanceTimersByTimeAsync(10 * 60_000);

    expect(stopped).toBe(true);
    expect(outbox.count()).toBe(0);
  });
});

import type { Envelope } from '@monitoreoti/shared';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { openCredential, parseCollectorConfig, SnmpPoller, type SnmpQueryRequest } from './snmp';

// Sellado por el servidor (SnmpCredentialSeal.Seal) para la credencial de abajo.
const CREDENTIAL = '7.vector-de-prueba-para-el-agente';
const SEALED = 'r3NPU067xs2SPPmlapuTfrcFADnvyzOl8rV+xfR2KxuieEabAJOuy9W/neMAeyeBr9AsSpthYnptXRktpJrtaGVhH+43ioymlA==';

const device = { id: 12, nombre: 'SW-ACC-02', ip: '10.10.0.2', tipo: 'switch', usuario: 'monitor', credencial: SEALED };

afterEach(() => vi.useRealTimers());

describe('recolector SNMP', () => {
  it('abre las claves que el servidor selló para este agente, y ningún otro puede', () => {
    expect(openCredential(CREDENTIAL, SEALED)).toEqual({ auth: 'clave-auth-1', priv: 'clave-priv-2' });
    expect(() => openCredential('7.otra-credencial', SEALED)).toThrow();
  });

  it('lee la lista de dispositivos del servidor', () => {
    expect(parseCollectorConfig(null)).toBeNull();
    expect(parseCollectorConfig({ intervalo_s: 300, dispositivos: [] })).toBeNull();
    expect(parseCollectorConfig({ intervalo_s: 120, dispositivos: [device, { id: 'x' }] })).toEqual({
      intervalSeconds: 120,
      devices: [device],
    });
  });

  it('consulta con las claves abiertas y manda el resultado sin ellas', async () => {
    const requests: SnmpQueryRequest[] = [];
    const sent: Envelope[] = [];
    const poller = new SnmpPoller({
      credential: CREDENTIAL,
      query: (request) => {
        requests.push(request);
        return Promise.resolve({ ok: true, sistema: { nombre: 'SW' }, interfaces: [] });
      },
      submit: (envelope) => sent.push(envelope),
      log: vi.fn(),
    });

    await poller.poll(device);

    expect(requests[0]).toMatchObject({ ip: '10.10.0.2', usuario: 'monitor', claveAutenticacion: 'clave-auth-1' });
    expect(sent[0]!.tipo).toBe('snmp_resultado');
    expect(sent[0]!.datos).toMatchObject({ dispositivoId: 12, ok: true });
    expect(JSON.stringify(sent[0])).not.toContain('clave-');
  });

  it('una impresora v2c se consulta con su comunidad, solo por sus consumibles', async () => {
    const printerSealed = 'pL6nOaxFcMLi6ZftkJgpdD2OJVJBxuxYCFID7JpofMveeMStoJdkiksiMgSt4wCLCjLGH4k8HXMNEaxhgDE=';
    const requests: SnmpQueryRequest[] = [];
    const sent: Envelope[] = [];
    const poller = new SnmpPoller({
      credential: CREDENTIAL,
      query: (request) => {
        requests.push(request);
        return Promise.resolve({ ok: true, consumibles: [{ indice: '1.1', nivel: 30, maximo: 100 }] });
      },
      submit: (envelope) => sent.push(envelope),
      log: vi.fn(),
    });

    await poller.poll({
      ...device,
      id: 40,
      usuario: '',
      credencial: printerSealed,
      version: 'v2c',
      origen: 'impresora',
    });

    expect(requests[0]).toEqual({
      ip: '10.10.0.2',
      version: 'v2c',
      comunidad: 'lectura-impresoras',
      consultas: ['consumibles'],
    });
    expect(sent[0]!.datos).toMatchObject({ maquinaId: 40, ok: true });
    expect(sent[0]!.datos).not.toHaveProperty('dispositivoId');
    expect(JSON.stringify(sent[0])).not.toContain('lectura-impresoras');
  });

  it('si las claves no abren, informa el fallo sin consultar', async () => {
    const query = vi.fn();
    const sent: Envelope[] = [];
    const poller = new SnmpPoller({ credential: '9.otra', query, submit: (e) => sent.push(e), log: vi.fn() });

    await poller.poll(device);

    expect(query).not.toHaveBeenCalled();
    expect(sent[0]!.datos).toMatchObject({ dispositivoId: 12, ok: false });
  });

  it('consulta cada dispositivo en su intervalo y deja de hacerlo al quitarle la lista', async () => {
    vi.useFakeTimers();
    const query = vi.fn(() => Promise.resolve({ ok: true }));
    const poller = new SnmpPoller({ credential: CREDENTIAL, query, submit: vi.fn(), log: vi.fn(), firstDelayMs: 1000 });

    poller.configure({ intervalSeconds: 60, devices: [device] });
    await vi.advanceTimersByTimeAsync(1000);
    expect(query).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(query).toHaveBeenCalledTimes(2);

    poller.configure(null);
    await vi.advanceTimersByTimeAsync(180_000);
    expect(query).toHaveBeenCalledTimes(2);
  });
});

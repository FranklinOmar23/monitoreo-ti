import { createCipheriv, createHash, hkdfSync, randomBytes } from 'node:crypto';
import type { Envelope } from '@monitoreoti/shared';
import { describe, expect, it, vi } from 'vitest';
import {
  collectFortiGate,
  describeHttpStatus,
  parseDevices,
  parseDhcpServers,
  parseLeases,
  testFortiApi,
  type FortiHttp,
  type FortiHttpRequest,
} from './fortigate';
import { openApiToken, parseCollectorConfig, SnmpPoller } from './snmp';

const CREDENTIAL = '7.vector-de-prueba-para-el-agente';
const TOKEN = 'h7k2m9p4q1w8e5r3t6y0u2i4o7p1as';
const API = { url: 'https://192.168.3.3:12443', token: TOKEN, acceptSelfSigned: true };

/** Lo mismo que SnmpCredentialSeal.Seal en el servidor. */
function seal(credential: string, plaintext: string): string {
  const hash = createHash('sha256').update(credential, 'utf8').digest('hex');
  const key = Buffer.from(hkdfSync('sha256', Buffer.from(hash, 'hex'), Buffer.alloc(0), 'monitoreoti/snmp/v1', 32));
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, nonce);
  const data = Buffer.concat([cipher.update(plaintext, 'utf8'), cipher.final()]);
  return Buffer.concat([nonce, data, cipher.getAuthTag()]).toString('base64');
}

/** FortiGate de mentira: responde según la ruta. */
function fakeFortiGate(routes: Record<string, { status: number; body: unknown } | { error: string }>) {
  const requests: FortiHttpRequest[] = [];
  const http: FortiHttp = (request) => {
    requests.push(request);
    const path = new URL(request.url).pathname;
    const route = routes[path];
    if (!route) return Promise.resolve({ status: 404, body: '' });
    if ('error' in route) return Promise.resolve(route);
    return Promise.resolve({ status: route.status, body: JSON.stringify(route.body) });
  };
  return { http, requests };
}

const STATUS = {
  serial: 'FG200GTK00000001',
  version: 'v7.4.4',
  build: 2662,
  results: { model_name: 'FortiGate', model_number: '200G', hostname: 'FGT-SD-01' },
};
const LEASES = {
  results: [
    {
      ip: '192.168.3.40',
      mac: 'aa:bb:cc:00:00:40',
      hostname: 'CONTA-PC03',
      expire_time: 1_900_000_000,
      interface: 'VLAN3-x3',
      type: 'ipv4',
      reserved: false,
      status: 'leased',
    },
    { ip: 'fe80::1', type: 'ipv6' },
  ],
};
const SERVERS = {
  results: [
    {
      id: 1,
      status: 'enable',
      interface: 'VLAN3-x3',
      'ip-range': [{ id: 1, 'start-ip': '192.168.3.100', 'end-ip': '192.168.3.200' }],
      'reserved-address': [{ id: 1, ip: '192.168.3.10', mac: '00:11:22:33:44:55', description: 'Impresora' }],
    },
    { id: 2, status: 'disable', interface: 'VLAN9', 'ip-range': [] },
  ],
};
const DEVICES = {
  results: [
    {
      ipv4_address: '192.168.3.40',
      mac: 'aa:bb:cc:00:00:40',
      hostname: 'CONTA-PC03',
      os_name: 'Windows',
      os_version: '10',
      is_online: true,
      detected_interface: 'VLAN3-x3',
    },
    { ipv4_address: '192.168.3.41', mac: 'aa:bb:cc:00:00:41', is_online: false },
  ],
};

describe('API del FortiGate', () => {
  it('prueba con system/status y dice modelo, versión, nombre y serie', async () => {
    const { http, requests } = fakeFortiGate({ '/api/v2/monitor/system/status': { status: 200, body: STATUS } });
    const result = await testFortiApi(http, API);

    expect(result).toMatchObject({
      ok: true,
      modelo: 'FortiGate 200G',
      version: 'v7.4.4 build 2662',
      nombre: 'FGT-SD-01',
      serie: 'FG200GTK00000001',
    });
    expect(requests[0]).toMatchObject({ url: 'https://192.168.3.3:12443/api/v2/monitor/system/status', token: TOKEN });
  });

  it('explica el 401 (token o Trusted Hosts) y el 403 (perfil)', async () => {
    const { http } = fakeFortiGate({ '/api/v2/monitor/system/status': { status: 401, body: {} } });
    const result = await testFortiApi(http, API);
    expect(result.ok).toBe(false);
    expect(result.error).toContain('Trusted Hosts');
    expect(describeHttpStatus(403)).toContain('perfil');
  });

  it('pasa el error de red tal cual (sin el token)', async () => {
    const { http } = fakeFortiGate({
      '/api/v2/monitor/system/status': { error: '192.168.3.3:12443 rechazó la conexión' },
    });
    const result = await testFortiApi(http, API);
    expect(result).toMatchObject({ ok: false, error: '192.168.3.3:12443 rechazó la conexión' });
    expect(JSON.stringify(result)).not.toContain(TOKEN);
  });

  it('lee concesiones IPv4, rangos y reservas de servidores activos, y solo dispositivos en línea', () => {
    expect(parseLeases(LEASES)).toEqual([
      {
        ip: '192.168.3.40',
        mac: 'aa:bb:cc:00:00:40',
        nombre: 'CONTA-PC03',
        vence: 1_900_000_000,
        interfaz: 'VLAN3-x3',
        reservada: false,
      },
    ]);
    expect(parseDhcpServers(SERVERS)).toEqual([
      {
        interfaz: 'VLAN3-x3',
        rangos: [{ desde: '192.168.3.100', hasta: '192.168.3.200' }],
        reservadas: [{ ip: '192.168.3.10', mac: '00:11:22:33:44:55', descripcion: 'Impresora' }],
      },
    ]);
    expect(parseDevices(DEVICES)).toEqual([
      {
        ip: '192.168.3.40',
        mac: 'aa:bb:cc:00:00:40',
        nombre: 'CONTA-PC03',
        sistema: 'Windows 10',
        interfaz: 'VLAN3-x3',
      },
    ]);
  });

  it('entiende el formato de FortiOS 6 (device/select con host y os anidados)', () => {
    expect(
      parseDevices({
        results: [{ ipv4_address: '10.0.0.5', host: { name: 'PC-5' }, os: { name: 'Android', version: '14' } }],
      }),
    ).toEqual([{ ip: '10.0.0.5', nombre: 'PC-5', sistema: 'Android 14', mac: undefined, interfaz: undefined }]);
  });

  it('con un permiso que falta manda lo demás y el aviso; si falla todo, es un error', async () => {
    const partial = fakeFortiGate({
      '/api/v2/monitor/system/dhcp': { status: 200, body: LEASES },
      '/api/v2/cmdb/system.dhcp/server': { status: 403, body: {} },
      '/api/v2/monitor/user/device/query': { status: 200, body: DEVICES },
    });
    const result = await collectFortiGate(partial.http, API);
    expect(result.ok).toBe(true);
    expect(result.error).toContain('Rangos DHCP');
    expect(result.servidoresDhcp).toBeUndefined();
    expect(result.dhcp).toHaveLength(1);
    expect(result.dispositivos).toHaveLength(1);

    const denied = fakeFortiGate({
      '/api/v2/monitor/system/dhcp': { status: 401, body: {} },
      '/api/v2/cmdb/system.dhcp/server': { status: 401, body: {} },
      '/api/v2/monitor/user/device/query': { status: 401, body: {} },
    });
    expect(await collectFortiGate(denied.http, API)).toMatchObject({ ok: false });
  });

  it('en FortiOS 6 (sin device/query) pide device/select', async () => {
    const old = fakeFortiGate({
      '/api/v2/monitor/system/dhcp': { status: 200, body: LEASES },
      '/api/v2/cmdb/system.dhcp/server': { status: 200, body: SERVERS },
      '/api/v2/monitor/user/device/select': { status: 200, body: DEVICES },
    });
    const result = await collectFortiGate(old.http, API);
    expect(result).toMatchObject({ ok: true, error: undefined });
    expect(old.requests.map((r) => new URL(r.url).pathname)).toContain('/api/v2/monitor/user/device/select');
  });

  it('el recolector lee la API después del SNMP con el token que selló el servidor, y no lo manda de vuelta', async () => {
    const sealed = seal(CREDENTIAL, JSON.stringify({ token: TOKEN }));
    expect(openApiToken(CREDENTIAL, sealed)).toBe(TOKEN);
    expect(() => openApiToken('7.otra', sealed)).toThrow();

    const config = parseCollectorConfig({
      intervalo_s: 300,
      dispositivos: [
        {
          id: 8,
          nombre: 'FGT-SD-01',
          ip: '192.168.3.3',
          tipo: 'firewall',
          usuario: 'Centinela',
          credencial: seal(CREDENTIAL, JSON.stringify({ auth: 'a'.repeat(8), priv: 'b'.repeat(8) })),
          api: { url: API.url, token: sealed, aceptar_certificado: true },
        },
      ],
    })!;
    expect(config.devices[0]!.api).toEqual({ url: API.url, token: sealed, acceptSelfSigned: true });

    const { http, requests } = fakeFortiGate({
      '/api/v2/monitor/system/dhcp': { status: 200, body: LEASES },
      '/api/v2/cmdb/system.dhcp/server': { status: 200, body: SERVERS },
      '/api/v2/monitor/user/device/query': { status: 200, body: DEVICES },
    });
    const sent: Envelope[] = [];
    const poller = new SnmpPoller({
      credential: CREDENTIAL,
      query: () => Promise.resolve({ ok: true }),
      submit: (e) => sent.push(e),
      log: vi.fn(),
      fortiHttp: http,
    });
    await poller.poll(config.devices[0]!);

    expect(sent.map((e) => e.tipo)).toEqual(['snmp_resultado', 'fortigate_datos']);
    expect(sent[1]!.datos).toMatchObject({ dispositivoId: 8, ok: true });
    expect(requests.every((r) => r.token === TOKEN)).toBe(true);
    expect(JSON.stringify(sent)).not.toContain(TOKEN);
  });
});

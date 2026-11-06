import { createCipheriv, createHash, hkdfSync, randomBytes } from 'node:crypto';
import type { Envelope } from '@monitoreoti/shared';
import { describe, expect, it, vi } from 'vitest';
import {
  applyDetectedDevices,
  applyPortStats,
  parseSwitchStatus,
  readFortiSwitches,
  switchToSnmpResult,
  type FortiHttp,
} from './fortigate';
import { parseCollectorConfig, SnmpPoller } from './snmp';

const CREDENTIAL = '7.vector-de-prueba-para-el-agente';
const TOKEN = 'h7k2m9p4q1w8e5r3t6y0u2i4o7p1as';
const API = { url: 'https://192.168.4.3:12443', token: TOKEN, acceptSelfSigned: true };

function seal(plaintext: string): string {
  const hash = createHash('sha256').update(CREDENTIAL, 'utf8').digest('hex');
  const key = Buffer.from(hkdfSync('sha256', Buffer.from(hash, 'hex'), Buffer.alloc(0), 'monitoreoti/snmp/v1', 32));
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, nonce);
  const data = Buffer.concat([cipher.update(plaintext, 'utf8'), cipher.final()]);
  return Buffer.concat([nonce, data, cipher.getAuthTag()]).toString('base64');
}

/** Como responde FortiOS 7.2 (managed-switch/status). */
const STATUS = {
  results: [
    {
      name: 'SW-PISO1',
      'switch-id': 'SW-PISO1',
      serial: 'S248EPTF18000001',
      status: 'Connected',
      state: 'Authorized',
      os_version: 'S248EP-v7.2.5-build0453,230227 (GA)',
      connecting_from: '10.255.1.2',
      ports: [
        { interface: 'port2', status: 'down', speed: 0 },
        { interface: 'port1', status: 'up', speed: 1000, duplex: 'full' },
        {
          interface: 'port3',
          status: 'up',
          speed: '1000full',
          isl_peer_device_name: 'SW-PISO2',
          isl_peer_port_name: 'port48',
        },
        { interface: 'port52', status: 'up', speed: 10000, fortilink_port: true },
        { interface: 'internal', status: 'up' },
      ],
    },
    { name: 'SW-PISO2', serial: 'S124EF0000000002', status: 'Disconnected', ports: [] },
  ],
};

const PORT_STATS = {
  results: [
    {
      serial: 'S248EPTF18000001',
      ports: {
        port1: { 'rx-bytes': 1000, 'tx-bytes': 2000, 'rx-errors': 3, 'tx-errors': 1, 'rx-drops': 2, 'tx-drops': 0 },
        port3: { rx_bytes: 50, tx_bytes: 60 },
      },
    },
  ],
};

const DETECTED = {
  results: [
    { mac: '3c:52:82:aa:00:01', switch_id: 'SW-PISO1', port_name: 'port1', vlan_id: 4 },
    { mac: '3c:52:82:aa:00:02', switch_id: 'S248EPTF18000001', port_name: 'port1', vlan_id: 4 },
    { mac: 'aa:bb:cc:00:00:99', switch_id: 'OTRO', port_name: 'port1' },
  ],
};

function fake(routes: Record<string, { status: number; body: unknown }>) {
  const http: FortiHttp = (request) => {
    const route = routes[new URL(request.url).pathname];
    return Promise.resolve(
      route ? { status: route.status, body: JSON.stringify(route.body) } : { status: 404, body: '' },
    );
  };
  return http;
}

describe('FortiSwitch por la API del FortiGate (decisión 80)', () => {
  it('lee cada switch con sus puertos ordenados, sin los que no son "portN"', () => {
    const switches = parseSwitchStatus(STATUS);
    expect(switches).toHaveLength(2);
    const [sw] = switches;
    expect(sw).toMatchObject({
      serie: 'S248EPTF18000001',
      nombre: 'SW-PISO1',
      modelo: 'S248EP',
      ip: '10.255.1.2',
      conectado: true,
    });
    expect(sw!.puertos.map((p) => p.numero)).toEqual([1, 2, 3, 52]);
    expect(sw!.puertos[0]).toMatchObject({ arriba: true, velocidad: 1000 });
    expect(sw!.puertos[2]).toMatchObject({ velocidad: 1000, isl: { equipo: 'SW-PISO2', puerto: 'port48' } });
    expect(sw!.puertos[3]!.fortilink).toBe(true);
    expect(switches[1]!.conectado).toBe(false);
  });

  it('suma contadores (con guion o guion bajo) y MAC por puerto del switch correcto', () => {
    const switches = parseSwitchStatus(STATUS);
    applyPortStats(switches, PORT_STATS);
    applyDetectedDevices(switches, DETECTED);
    expect(switches[0]!.puertos[0]).toMatchObject({
      bytesIn: 1000,
      bytesOut: 2000,
      erroresIn: 3,
      erroresOut: 1,
      descartes: 2,
    });
    expect(switches[0]!.puertos[2]).toMatchObject({ bytesIn: 50, bytesOut: 60 });
    expect(switches[0]!.macs).toEqual([
      { puerto: 1, mac: '3c:52:82:aa:00:01', vlan: 4 },
      { puerto: 1, mac: '3c:52:82:aa:00:02', vlan: 4 },
    ]);
  });

  it('lo convierte en un resultado de sondeo como el de SNMP', () => {
    const switches = parseSwitchStatus(STATUS);
    applyDetectedDevices(switches, DETECTED);
    const result = switchToSnmpResult(41, 'S248EPTF18000001', switches[0], 'FGT-SD-02');
    expect(result).toMatchObject({ dispositivoId: 41, ok: true, sistema: { nombre: 'SW-PISO1' } });
    const interfaces = result['interfaces'] as { indice: number; operativo: string; tipo: number }[];
    expect(interfaces.map((i) => [i.indice, i.operativo, i.tipo])).toEqual([
      [1, 'up', 6],
      [2, 'down', 6],
      [3, 'up', 6],
      [52, 'up', 6],
    ]);
    expect(result['mac']).toHaveLength(2);
    expect(result['lldp']).toEqual([
      { puertoLocal: 3, sistema: 'SW-PISO2', puerto: 'port48' },
      { puertoLocal: 52, sistema: 'FGT-SD-02', puerto: null },
    ]);

    expect(switchToSnmpResult(42, 'S124EF0000000002', switches[1], 'FGT')).toMatchObject({
      ok: false,
      error: 'El FortiGate dice que el switch está desconectado.',
    });
    expect(switchToSnmpResult(43, 'NOEXISTE', undefined, 'FGT')['error']).toContain('NOEXISTE');
  });

  it('el estado es obligatorio; sin contadores ni MAC sigue con aviso, y dice qué campos trajo', async () => {
    const read = await readFortiSwitches(
      fake({ '/api/v2/monitor/switch-controller/managed-switch/status': { status: 200, body: STATUS } }),
      API,
    );
    expect(read.ok).toBe(true);
    expect(read.error).toContain('contadores de puertos');
    expect(read.campos).toContain('serial');
    expect(read.campos).toContain('puerto.interface');

    const denied = await readFortiSwitches(
      fake({ '/api/v2/monitor/switch-controller/managed-switch/status': { status: 403, body: {} } }),
      API,
    );
    expect(denied).toMatchObject({ ok: false, switches: [] });
    expect(denied.error).toContain('403');
  });

  it('después de leer el FortiGate, el recolector manda un sondeo por cada switch registrado', async () => {
    const config = parseCollectorConfig({
      intervalo_s: 300,
      dispositivos: [
        {
          id: 8,
          nombre: 'FGT-SD-02',
          ip: '192.168.4.3',
          tipo: 'firewall',
          usuario: 'Centinela',
          credencial: seal(JSON.stringify({ auth: 'a'.repeat(8), priv: 'b'.repeat(8) })),
          api: {
            url: API.url,
            token: seal(JSON.stringify({ token: TOKEN })),
            aceptar_certificado: true,
            switches: [
              { id: 41, serie: 's248eptf18000001' },
              { id: 42, serie: 'S124EF0000000002' },
            ],
          },
        },
      ],
    })!;
    expect(config.devices[0]!.api!.switches).toHaveLength(2);

    const sent: Envelope[] = [];
    const poller = new SnmpPoller({
      credential: CREDENTIAL,
      query: () => Promise.resolve({ ok: true }),
      submit: (e) => sent.push(e),
      log: vi.fn(),
      fortiHttp: fake({
        '/api/v2/monitor/switch-controller/managed-switch/status': { status: 200, body: STATUS },
        '/api/v2/monitor/switch-controller/detected-device': { status: 200, body: DETECTED },
      }),
    });
    await poller.poll(config.devices[0]!);

    const switchResults = sent
      .filter((e) => e.tipo === 'snmp_resultado')
      .map((e) => e.datos as Record<string, unknown>);
    expect(switchResults.map((d) => [d['dispositivoId'], d['ok']])).toEqual([
      [8, true],
      [41, true],
      [42, false],
    ]);
    expect(JSON.stringify(sent)).not.toContain(TOKEN);
  });
});

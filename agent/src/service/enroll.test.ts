import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { CredentialStore, type Protector, type StoredCredential } from './credential-store';
import { EnrollmentRejectedError, enrollAgent } from './enroll';
import { describeOperatingSystem, isVirtualMac, pickPrimaryInterface } from './machine-info';

const identity = {
  hostname: 'CONTA-03',
  macAddress: '3C:52:82:A1:00:03',
  ipAddress: '10.10.20.31',
  operatingSystem: 'Windows 11 (10.0.22631)',
};
const serverUrl = new URL('http://localhost:5126');

const jsonResponse = (body: unknown, status = 200) =>
  Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

describe('registro del agente', () => {
  it('envía el token y la identidad y devuelve la credencial', async () => {
    let sent: { url: string; body: Record<string, unknown> } | undefined;
    const credential = await enrollAgent({
      serverUrl,
      enrollToken: '1.secreto',
      agentVersion: '0.1.0',
      identity,
      fetchImpl: (input, init) => {
        sent = { url: String(input), body: JSON.parse(String(init?.body)) as Record<string, unknown> };
        return jsonResponse({ credential: '1.abc', machineId: 7, agentId: 3, companyPublicKey: null });
      },
    });

    expect(sent?.url).toBe('http://localhost:5126/agent/register');
    expect(sent?.body).toMatchObject({
      enrollToken: '1.secreto',
      hostname: 'CONTA-03',
      macAddress: '3C:52:82:A1:00:03',
    });
    expect(credential).toEqual({
      serverOrigin: 'http://localhost:5126',
      credential: '1.abc',
      machineId: 7,
      agentId: 3,
      companyPublicKey: null,
    });
  });

  it('distingue el rechazo del token de un fallo temporal', async () => {
    const base = { serverUrl, enrollToken: 'x', agentVersion: '0.1.0', identity };

    await expect(enrollAgent({ ...base, fetchImpl: () => jsonResponse({}, 401) })).rejects.toBeInstanceOf(
      EnrollmentRejectedError,
    );
    await expect(enrollAgent({ ...base, fetchImpl: () => jsonResponse({}, 503) })).rejects.toThrow(/HTTP 503/);
    await expect(enrollAgent({ ...base, fetchImpl: () => jsonResponse({ otra: 'cosa' }) })).rejects.toThrow(
      /inesperada/,
    );
  });
});

describe('identidad del equipo', () => {
  const nic = (mac: string, address: string, extra: Record<string, unknown> = {}) => ({
    mac,
    address,
    family: 'IPv4' as const,
    internal: false,
    netmask: '255.255.255.0',
    cidr: null,
    ...extra,
  });

  it('elige el primer adaptador físico con IPv4 y descarta los virtuales', () => {
    const primary = pickPrimaryInterface({
      'vEthernet (WSL)': [nic('00:15:5d:00:00:01', '172.20.0.1')],
      Loopback: [nic('00:00:00:00:00:00', '127.0.0.1', { internal: true })],
      Ethernet: [nic('3c:52:82:a1:00:03', '10.10.20.31')],
      'Wi-Fi': [nic('3c:52:82:a1:f0:03', '10.10.20.99')],
    });

    expect(primary).toEqual({ mac: '3C:52:82:A1:00:03', ip: '10.10.20.31' });
  });

  it('descarta adaptadores con MAC virtual aunque se llamen "Ethernet" (caso real: VirtualBox host-only)', () => {
    const interfaces = {
      'Ethernet 3': [nic('0a:00:27:00:00:0b', '192.168.56.1')],
      'Ethernet 2': [nic('02:50:f2:93:9d:00', '169.254.28.101')],
      Hamachi: [nic('7a:79:19:1b:4d:b4', '25.27.77.180')],
      'Ethernet 4': [nic('74:86:e2:2e:bd:bc', '192.168.3.115')],
    };

    expect(pickPrimaryInterface(interfaces)).toEqual({ mac: '74:86:E2:2E:BD:BC', ip: '192.168.3.115' });
  });

  it('prefiere el adaptador que Windows indica como principal', () => {
    const interfaces = {
      Ethernet: [nic('3c:52:82:a1:00:03', '10.10.20.31')],
      'Wi-Fi': [nic('3c:52:82:a1:f0:03', '10.10.20.99')],
    };

    expect(pickPrimaryInterface(interfaces, 'Wi-Fi')?.mac).toBe('3C:52:82:A1:F0:03');
    expect(pickPrimaryInterface(interfaces, 'No existe')?.mac).toBe('3C:52:82:A1:00:03');
  });

  it('reconoce MAC virtuales por prefijo y por el bit de administración local', () => {
    expect(isVirtualMac('00:15:5D:00:00:01')).toBe(true); // Hyper-V
    expect(isVirtualMac('08:00:27:00:00:01')).toBe(true); // VirtualBox
    expect(isVirtualMac('0A:00:27:00:00:0B')).toBe(true); // administrada localmente
    expect(isVirtualMac('74:86:E2:2E:BD:BC')).toBe(false);
  });

  it('sin adaptador físico no inventa una identidad', () => {
    expect(pickPrimaryInterface({ VirtualBox: [nic('0a:00:27:00:00:01', '192.168.56.1')] })).toBeUndefined();
  });

  it('distingue Windows 10 de Windows 11 por la compilación', () => {
    expect(describeOperatingSystem('Windows_NT', '10.0.19045')).toBe('Windows 10 (10.0.19045)');
    expect(describeOperatingSystem('Windows_NT', '10.0.22631')).toBe('Windows 11 (10.0.22631)');
  });
});

describe('almacén de la credencial', () => {
  let dir: string;
  // Protector de prueba reversible y reconocible: comprueba que lo guardado pasa por el cifrado.
  const fakeProtector: Protector = {
    protect: (data) => Promise.resolve(Buffer.from(data.toString('base64').split('').reverse().join(''))),
    unprotect: (data) => Promise.resolve(Buffer.from(data.toString().split('').reverse().join(''), 'base64')),
  };
  const value: StoredCredential = {
    serverOrigin: 'http://localhost:5126',
    credential: '1.supersecreto',
    machineId: 7,
    agentId: 3,
    companyPublicKey: null,
  };

  beforeEach(async () => {
    dir = await mkdtemp(join(tmpdir(), 'monitoreoti-'));
  });
  afterEach(() => rm(dir, { recursive: true, force: true }));

  it('guarda cifrado y recupera la misma credencial', async () => {
    const store = new CredentialStore(join(dir, 'datos'), fakeProtector);

    expect(await store.load()).toBeNull();
    await store.save(value);

    expect((await readFile(join(dir, 'datos', 'credential.bin'))).toString()).not.toContain('supersecreto');
    expect(await store.load()).toEqual(value);
  });

  it('trata un archivo ilegible como si no hubiera credencial', async () => {
    const store = new CredentialStore(dir, fakeProtector);
    await store.save(value);
    const other: Protector = { ...fakeProtector, unprotect: () => Promise.reject(new Error('otro equipo')) };

    expect(await new CredentialStore(dir, other).load()).toBeNull();
  });
});

describe.runIf(process.platform === 'win32')('DPAPI real', () => {
  it('cifra y descifra con el ámbito de máquina', async () => {
    const { dpapiProtector } = await import('./credential-store');
    const plain = Buffer.from('credencial-de-prueba');

    const encrypted = await dpapiProtector.protect(plain);

    expect(encrypted.toString()).not.toContain('credencial');
    expect((await dpapiProtector.unprotect(encrypted)).toString()).toBe('credencial-de-prueba');
  });
});

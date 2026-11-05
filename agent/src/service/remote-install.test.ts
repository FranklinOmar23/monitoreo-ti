import { createCipheriv, createHash, generateKeyPairSync, hkdfSync, randomBytes, sign } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { Envelope } from '@monitoreoti/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { parseInstallRequest, RemoteInstaller, type HostInstallRequest } from './remote-install';
import { updatePayload } from './updater';

const CREDENTIAL = '7.vector-de-prueba-para-el-agente';
const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const publicKeyBase64 = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const installer = Buffer.from('instalador de prueba');
const sha256 = createHash('sha256').update(installer).digest('hex');

/** Lo mismo que SnmpCredentialSeal.Seal en el servidor. */
function seal(plaintext: string, credential = CREDENTIAL): string {
  const hash = createHash('sha256').update(credential, 'utf8').digest('hex');
  const key = Buffer.from(hkdfSync('sha256', Buffer.from(hash, 'hex'), Buffer.alloc(0), 'monitoreoti/snmp/v1', 32));
  const nonce = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, nonce);
  const data = Buffer.concat([cipher.update(plaintext, 'utf8'), cipher.final()]);
  return Buffer.concat([nonce, data, cipher.getAuthTag()]).toString('base64');
}

function request(overrides: Record<string, unknown> = {}) {
  const version = '0.3.7';
  return {
    instalacion_id: 31,
    ip: '192.168.4.6',
    cuenta: seal(JSON.stringify({ usuario: 'CARIBE\\soporte', clave: 'Clave-Secreta-1' })),
    servidor: 'https://192.168.3.115:5127',
    token: seal(JSON.stringify({ token: '1.tok-un-solo-uso' })),
    instalador: {
      version,
      url: '/agent/actualizacion',
      sha256,
      tamano: installer.length,
      firma: sign('sha256', Buffer.from(updatePayload(version, sha256)), {
        key: privateKey,
        dsaEncoding: 'ieee-p1363',
      }).toString('base64'),
    },
    ...overrides,
  };
}

let dir: string;
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'monitoreoti-remote-'));
});
afterEach(() => rmSync(dir, { recursive: true, force: true }));

function setup(steps: { estado: string; detalle?: string; sistema?: string; fin?: boolean }[]) {
  const sent: Envelope[] = [];
  const hostRequests: HostInstallRequest[] = [];
  const log = vi.fn();
  const fetchImpl = vi.fn(() => Promise.resolve(new Response(new Uint8Array(installer))));
  const remote = new RemoteInstaller({
    serverUrl: new URL('https://192.168.3.115:5127'),
    credential: CREDENTIAL,
    dataDir: dir,
    install: (req, onStep) => {
      hostRequests.push(req);
      for (const step of steps) onStep(step);
      return Promise.resolve();
    },
    submit: (e) => sent.push(e),
    log,
    fetchImpl: fetchImpl as unknown as typeof fetch,
    publicKey: publicKeyBase64,
  });
  return { remote, sent, hostRequests, log, fetchImpl };
}

describe('instalación remota del agente (decisión 77)', () => {
  it('valida el pedido del servidor', () => {
    expect(parseInstallRequest(request())).toMatchObject({ installId: 31, ip: '192.168.4.6' });
    expect(parseInstallRequest(request({ ip: 'no-es-ip' }))).toBeNull();
    expect(
      parseInstallRequest({ ...request(), instalador: { ...request().instalador, url: 'https://otro' } }),
    ).toBeNull();
  });

  it('abre la cuenta y el token, descarga el instalador verificado una vez e informa cada paso sin la clave', async () => {
    const { remote, sent, hostRequests, log, fetchImpl } = setup([
      { estado: 'conectando' },
      { estado: 'copiando', sistema: 'Windows 10 (10.0.19045) 64 bits' },
      { estado: 'instalando' },
      { estado: 'instalado', fin: true },
    ]);

    await remote.handle(request());
    await remote.handle(request({ instalacion_id: 32, ip: '192.168.4.7' }));

    expect(hostRequests[0]).toMatchObject({
      ip: '192.168.4.6',
      usuario: 'CARIBE\\soporte',
      clave: 'Clave-Secreta-1',
      servidor: 'https://192.168.3.115:5127',
      token: '1.tok-un-solo-uso',
    });
    // La segunda instalación usa el instalador en caché.
    expect(fetchImpl).toHaveBeenCalledTimes(1);
    expect(hostRequests[1]!.instalador).toBe(hostRequests[0]!.instalador);

    const states = sent.filter((e) => (e.datos as { instalacionId: number }).instalacionId === 31).map((e) => e.datos);
    expect(states.map((d) => (d as { estado: string }).estado)).toEqual([
      'conectando',
      'copiando',
      'instalando',
      'instalado',
    ]);
    expect(sent[0]!.tipo).toBe('instalacion_estado');
    expect(JSON.stringify(sent)).not.toContain('Clave-Secreta-1');
    expect(JSON.stringify(log.mock.calls)).not.toContain('Clave-Secreta-1');
    expect(JSON.stringify(log.mock.calls)).not.toContain('tok-un-solo-uso');
  });

  it('no instala un archivo que no firmó el producto', async () => {
    const { remote, sent, hostRequests } = setup([]);
    await remote.handle(
      request({ instalador: { ...request().instalador, firma: Buffer.alloc(64).toString('base64') } }),
    );
    expect(hostRequests).toHaveLength(0);
    expect(sent[0]!.datos).toMatchObject({ instalacionId: 31, estado: 'error' });
  });

  it('si la cuenta no abre (sellada para otro agente) lo informa sin intentar', async () => {
    const { remote, sent, hostRequests } = setup([]);
    await remote.handle(request({ cuenta: seal('{"usuario":"x","clave":"y"}', '9.otro') }));
    expect(hostRequests).toHaveLength(0);
    expect((sent[0]!.datos as { detalle: string }).detalle).toContain('No se pudieron abrir');
  });
});

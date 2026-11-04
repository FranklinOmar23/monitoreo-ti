import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AgentUpdater, isNewer, parseOffer, updatePayload, verifyUpdate } from './updater';

const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const publicKeyBase64 = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const installer = Buffer.from('instalador de prueba');
const sha256 = createHash('sha256').update(installer).digest('hex');

function offer(overrides: Record<string, unknown> = {}, key = privateKey) {
  const version = String(overrides['version'] ?? '0.2.0');
  const hash = String(overrides['sha256'] ?? sha256);
  return {
    version,
    url: '/agent/actualizacion',
    sha256: hash,
    tamano: installer.length,
    firma: sign('sha256', Buffer.from(updatePayload(version, hash)), { key, dsaEncoding: 'ieee-p1363' }).toString(
      'base64',
    ),
    ...overrides,
  };
}

let dir: string;
beforeEach(() => {
  dir = mkdtempSync(join(tmpdir(), 'monitoreoti-update-'));
});
afterEach(() => rmSync(dir, { recursive: true, force: true }));

function setup(body: Buffer = installer) {
  const fetchImpl = vi.fn(() => Promise.resolve(new Response(new Uint8Array(body))));
  const handOver = vi.fn();
  const log = vi.fn();
  const updater = new AgentUpdater({
    serverUrl: new URL('https://monitoreo.ejemplo.do'),
    credential: '1.abc',
    currentVersion: '0.1.0',
    dataDir: dir,
    handOver,
    log,
    fetchImpl: fetchImpl as unknown as typeof fetch,
    publicKey: publicKeyBase64,
  });
  return { updater, fetchImpl, handOver, log };
}

describe('actualizador del agente', () => {
  it('compara versiones por números y valida la oferta', () => {
    expect(isNewer('0.10.0', '0.9.9')).toBe(true);
    expect(isNewer('0.1.0', '0.1.0')).toBe(false);
    expect(parseOffer({ ...offer(), url: 'https://otro/archivo.exe' })).toBeNull();
    expect(parseOffer({ ...offer(), version: '1.0; rm -rf' })).toBeNull();
    expect(verifyUpdate(offer(), publicKeyBase64)).toBe(true);
  });

  it('descarga con su credencial, comprueba el hash y le pasa la instalación al anfitrión', async () => {
    const { updater, fetchImpl, handOver } = setup();
    await updater.handle(offer());

    expect(fetchImpl).toHaveBeenCalledWith(new URL('https://monitoreo.ejemplo.do/agent/actualizacion'), {
      headers: { Authorization: 'Bearer 1.abc' },
    });
    const pending = JSON.parse(readFileSync(join(dir, 'update-pending.json'), 'utf8')) as Record<string, string>;
    expect(pending).toMatchObject({ version: '0.2.0', sha256 });
    expect(readFileSync(pending['file']!)).toEqual(installer);
    expect(handOver).toHaveBeenCalledOnce();
  });

  it('rechaza una versión firmada con otra llave sin descargar nada', async () => {
    const { updater, fetchImpl, handOver } = setup();
    await updater.handle(offer({}, generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey));

    expect(fetchImpl).not.toHaveBeenCalled();
    expect(handOver).not.toHaveBeenCalled();
  });

  it('no instala un archivo que no coincide con el hash publicado', async () => {
    const tampered = Buffer.from('instalador alterado!');
    const { updater, handOver } = setup(tampered);
    await updater.handle(offer());

    expect(handOver).not.toHaveBeenCalled();
    expect(existsSync(join(dir, 'update-pending.json'))).toBe(false);
  });

  it('ignora una versión que no es más nueva', async () => {
    const { updater, fetchImpl } = setup();
    await updater.handle(offer({ version: '0.1.0' }));
    expect(fetchImpl).not.toHaveBeenCalled();
  });
});

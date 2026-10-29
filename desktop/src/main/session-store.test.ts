import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { isValidRefreshToken, SessionStore, type Encryptor } from './session-store';

const TOKEN = 'abcDEF123_-abcDEF123_-abcDEF123_-abcDEF123';

const reversible: Encryptor = {
  isAvailable: () => true,
  encrypt: (plain) => Buffer.from([...plain].reverse().join('')),
  decrypt: (data) => [...data.toString()].reverse().join(''),
};

describe('SessionStore', () => {
  let dir: string;
  beforeEach(async () => {
    dir = await mkdtemp(join(tmpdir(), 'sesion-'));
  });
  afterEach(() => rm(dir, { recursive: true, force: true }));

  it('guarda cifrado, recupera y borra el refresh token', async () => {
    const store = new SessionStore(join(dir, 'sub', 'session.bin'), reversible);

    expect(await store.load()).toBeNull();
    await store.save(TOKEN);
    expect((await readFile(join(dir, 'sub', 'session.bin'))).toString()).not.toBe(TOKEN);
    expect(await store.load()).toBe(TOKEN);

    await store.clear();
    expect(await store.load()).toBeNull();
  });

  it('sin cifrado disponible no guarda nada en disco', async () => {
    const store = new SessionStore(join(dir, 'session.bin'), { ...reversible, isAvailable: () => false });

    await store.save(TOKEN);

    expect(await store.load()).toBeNull();
    await expect(readFile(join(dir, 'session.bin'))).rejects.toThrow();
  });

  it('rechaza valores que no parecen un refresh token (validación del mensaje IPC)', async () => {
    const store = new SessionStore(join(dir, 'session.bin'), reversible);

    for (const bad of ['', 'corto', 'con espacios y símbolos <script>', 'x'.repeat(500), 42, null]) {
      expect(isValidRefreshToken(bad)).toBe(false);
    }
    await expect(store.save('../../etc')).rejects.toThrow(/inválido/);
  });
});

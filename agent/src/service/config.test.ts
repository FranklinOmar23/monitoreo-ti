import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { AGENT_VERSION, loadServiceConfig, readConfigFile, removeEnrollToken } from './config';

const noFile = () => ({});

describe('configuración del servicio', () => {
  it('usa valores por defecto de desarrollo', () => {
    const config = loadServiceConfig({}, noFile);

    expect(config.serverUrl.href).toBe('http://localhost:5126/');
    expect(config.heartbeatSeconds).toBe(30);
  });

  it('acepta HTTPS hacia cualquier servidor', () => {
    const config = loadServiceConfig({ MONITOREOTI_SERVER_URL: 'https://api.monitoreoti.do' }, noFile);

    expect(config.serverUrl.hostname).toBe('api.monitoreoti.do');
  });

  it('rechaza HTTP fuera del propio equipo', () => {
    expect(() => loadServiceConfig({ MONITOREOTI_SERVER_URL: 'http://192.168.4.117:5126' }, noFile)).toThrow(/HTTPS/);
  });

  it('rechaza URL y frecuencias inválidas', () => {
    expect(() => loadServiceConfig({ MONITOREOTI_SERVER_URL: 'no-es-url' }, noFile)).toThrow(/URL/);
    expect(() => loadServiceConfig({ MONITOREOTI_HEARTBEAT_SECONDS: '1' }, noFile)).toThrow(/entre 5 y 300/);
    expect(() => loadServiceConfig({ MONITOREOTI_HEARTBEAT_SECONDS: 'abc' }, noFile)).toThrow(/entre 5 y 300/);
  });
});

describe('config.json del instalador', () => {
  it('toma la URL y el token del archivo; las variables de entorno tienen prioridad', () => {
    const file = () => ({ serverUrl: 'https://api.monitoreoti.do', enrollToken: ' 1.TOKEN ' });

    const fromFile = loadServiceConfig({}, file);
    expect(fromFile.serverUrl.origin).toBe('https://api.monitoreoti.do');
    expect(fromFile.enrollToken).toBe('1.TOKEN');

    const fromEnv = loadServiceConfig({ MONITOREOTI_SERVER_URL: 'https://otro.example' }, file);
    expect(fromEnv.serverUrl.origin).toBe('https://otro.example');
  });

  it('lee el archivo real, tolera que falte o esté dañado y borra el token tras registrarse', () => {
    const dir = mkdtempSync(join(tmpdir(), 'cfg-'));
    try {
      expect(readConfigFile(dir)).toEqual({});
      writeFileSync(join(dir, 'config.json'), '{no es json');
      expect(readConfigFile(dir)).toEqual({});

      writeFileSync(join(dir, 'config.json'), JSON.stringify({ serverUrl: 'https://a.example', enrollToken: '1.X' }));
      expect(readConfigFile(dir)).toEqual({ serverUrl: 'https://a.example', enrollToken: '1.X' });

      removeEnrollToken(dir);
      expect(JSON.parse(readFileSync(join(dir, 'config.json'), 'utf8'))).toEqual({ serverUrl: 'https://a.example' });
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe('versión del agente', () => {
  it('coincide con la del paquete (la del instalador): si no, pediría actualizarse sin fin', () => {
    const pkg = JSON.parse(readFileSync(join(__dirname, '..', '..', 'package.json'), 'utf8')) as { version: string };
    expect(AGENT_VERSION).toBe(pkg.version);
  });
});

import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEFAULT_SERVER_URL, normalizeServerUrl, resolveServerUrl } from './server-config';
import { criticalLabel, trayIconBitmap } from './tray-icon';
import { updateFeedUrl } from './updates';

describe('servidor del panel instalado', () => {
  it('toma la variable, si no el panel.json del instalador, si no el local', () => {
    const dir = mkdtempSync(join(tmpdir(), 'panel-'));
    expect(resolveServerUrl({}, dir)).toBe(DEFAULT_SERVER_URL);
    writeFileSync(join(dir, 'panel.json'), JSON.stringify({ servidor: 'https://ti.caribe.do/' }));
    expect(resolveServerUrl({}, dir)).toBe('https://ti.caribe.do');
    expect(resolveServerUrl({ MONITOREOTI_SERVER_URL: 'http://192.168.1.5:5126' }, dir)).toBe(
      'http://192.168.1.5:5126',
    );
  });

  it('solo acepta http y https', () => {
    expect(normalizeServerUrl('file:///C:/x')).toBeNull();
    expect(normalizeServerUrl('javascript:alert(1)')).toBeNull();
    expect(normalizeServerUrl('https://ti.caribe.do/ruta?x=1')).toBe('https://ti.caribe.do');
  });

  it('las actualizaciones se piden al mismo servidor', () => {
    expect(updateFeedUrl('https://ti.caribe.do/')).toBe('https://ti.caribe.do/api/v1/panel/actualizacion');
  });
});

describe('ícono de la bandeja', () => {
  it('tiene el tamaño pedido, esquinas transparentes y un punto rojo solo con alertas críticas', () => {
    const plain = trayIconBitmap(32, false);
    const badge = trayIconBitmap(32, true);
    expect(plain.length).toBe(32 * 32 * 4);
    expect(plain[3]).toBe(0); // esquina superior izquierda transparente
    const pixel = (data: Buffer, x: number, y: number) => {
      const o = (y * 32 + x) * 4;
      return { b: data[o]!, g: data[o + 1]!, r: data[o + 2]!, a: data[o + 3]! };
    };
    const redSpot = pixel(badge, 24, 24);
    expect(redSpot.r).toBeGreaterThan(200);
    expect(redSpot.g).toBeLessThan(60);
    expect(pixel(plain, 24, 24).r).toBeLessThan(200);
  });

  it('dice cuántas alertas críticas hay', () => {
    expect(criticalLabel(0)).toBe('Sin alertas críticas');
    expect(criticalLabel(1)).toBe('1 alerta crítica');
    expect(criticalLabel(3)).toBe('3 alertas críticas');
  });
});

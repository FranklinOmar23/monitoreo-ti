import { afterEach, describe, expect, it } from 'vitest';
import { applyBrandColor, brandTones, cacheAppearance, cachedAppearance, contrast } from './brand';

afterEach(() => {
  applyBrandColor(null);
  localStorage.clear();
});

describe('color de la empresa (W-40)', () => {
  it('se lee siempre: sobre blanco, sobre el fondo oscuro, y el texto del botón', () => {
    for (const color of ['#0f766e', '#ffeb3b', '#00ff00', '#1d4ed8', '#000000', '#ffffff', '#e11d48']) {
      const { light, dark } = brandTones(color);
      expect(contrast(light.primary, '#ffffff')).toBeGreaterThanOrEqual(4.5);
      expect(contrast(dark.primary, '#141a1f')).toBeGreaterThanOrEqual(4.5);
      expect(contrast(light.primary, light.onPrimary)).toBeGreaterThanOrEqual(3);
      expect(contrast(dark.primary, dark.onPrimary)).toBeGreaterThanOrEqual(3);
    }
  });

  it('un color que ya se lee bien no se toca', () => {
    expect(brandTones('#1D4ED8').light.primary).toBe('#1d4ed8');
  });

  it('pinta el panel y vuelve a los colores de Centinela', () => {
    applyBrandColor('#1d4ed8');
    const style = document.getElementById('marca-empresa');
    expect(style?.textContent).toContain('--color-primary: #1d4ed8');
    expect(style?.textContent).toContain('prefers-color-scheme: dark');
    applyBrandColor(null);
    expect(document.getElementById('marca-empresa')).toBeNull();
    applyBrandColor('rojo');
    expect(document.getElementById('marca-empresa')).toBeNull();
  });

  it('recuerda la última apariencia para la pantalla de carga y descarta lo que no es una imagen', () => {
    cacheAppearance({ color: '#1d4ed8', logo: 'data:image/png;base64,AAAA' });
    expect(cachedAppearance()).toEqual({ color: '#1d4ed8', logo: 'data:image/png;base64,AAAA' });
    localStorage.setItem('centinela.apariencia', JSON.stringify({ color: 'x', logo: 'javascript:alert(1)' }));
    expect(cachedAppearance()).toEqual({ color: null, logo: null });
    cacheAppearance({ color: null, logo: null });
    expect(cachedAppearance()).toBeNull();
  });
});

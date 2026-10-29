// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { buildContentSecurityPolicy, isAllowedNavigation } from './security';

describe('CSP del panel', () => {
  it('en producción solo permite el propio panel y el servidor configurado', () => {
    const csp = buildContentSecurityPolicy({ serverUrl: 'https://api.monitoreoti.do' });

    expect(csp).toContain("script-src 'self'");
    expect(csp).toContain("connect-src 'self' https://api.monitoreoti.do wss://api.monitoreoti.do");
    expect(csp).toContain("object-src 'none'");
    expect(csp).not.toContain('unsafe-inline');
    expect(csp).not.toContain('unsafe-eval');
  });

  it('en desarrollo agrega el servidor de Vite pero nunca eval', () => {
    const csp = buildContentSecurityPolicy({
      serverUrl: 'http://localhost:5126',
      devServerUrl: 'http://localhost:5173',
    });

    expect(csp).toContain('ws://localhost:5126');
    expect(csp).toContain('ws://localhost:5173');
    expect(csp).toContain("script-src 'self' 'unsafe-inline'");
    expect(csp).not.toContain('unsafe-eval');
  });
});

describe('navegación', () => {
  it('permite rutas del propio panel y bloquea sitios externos', () => {
    expect(isAllowedNavigation('http://localhost:5173/equipos', 'http://localhost:5173/')).toBe(true);
    expect(isAllowedNavigation('https://example.com/', 'http://localhost:5173/')).toBe(false);
    expect(isAllowedNavigation('javascript:alert(1)', 'http://localhost:5173/')).toBe(false);
    expect(isAllowedNavigation('no es url', 'http://localhost:5173/')).toBe(false);
  });

  it('con archivos locales solo permite el index del panel', () => {
    const app = 'file:///C:/Program%20Files/MonitoreoTI/resources/app/out/renderer/index.html';

    expect(isAllowedNavigation(app, app)).toBe(true);
    expect(isAllowedNavigation('file:///C:/Windows/System32/drivers/etc/hosts', app)).toBe(false);
  });
});

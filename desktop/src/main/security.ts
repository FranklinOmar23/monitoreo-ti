/**
 * Reglas de seguridad del panel (sección 12.1): CSP estricta y navegación bloqueada.
 * Funciones puras para poder probarlas sin Electron.
 */

export interface CspOptions {
  /** URL base del servidor MonitoreoTI (API y SignalR). */
  serverUrl: string;
  /** En desarrollo, el servidor de Vite sirve el renderer y necesita HMR por WebSocket. */
  devServerUrl?: string;
}

export function buildContentSecurityPolicy({ serverUrl, devServerUrl }: CspOptions): string {
  const server = new URL(serverUrl);
  const serverOrigin = server.origin;
  const serverSocket = `${server.protocol === 'https:' ? 'wss:' : 'ws:'}//${server.host}`;

  const connect = ["'self'", serverOrigin, serverSocket];
  const scripts = ["'self'"];
  const styles = ["'self'"];

  if (devServerUrl) {
    const dev = new URL(devServerUrl);
    connect.push(dev.origin, `ws://${dev.host}`);
    // Solo en desarrollo: Vite inyecta estilos con <style> y plugin-react un script inline para la recarga en caliente.
    scripts.push("'unsafe-inline'");
    styles.push("'unsafe-inline'");
  }

  return [
    "default-src 'self'",
    `script-src ${scripts.join(' ')}`,
    `style-src ${styles.join(' ')}`,
    "img-src 'self' data: blob:",
    "font-src 'self'",
    `connect-src ${connect.join(' ')}`,
    "object-src 'none'",
    "base-uri 'none'",
    "form-action 'none'",
    "frame-ancestors 'none'",
  ].join('; ');
}

/** Solo se permite navegar dentro de la propia aplicación. */
export function isAllowedNavigation(targetUrl: string, appUrl: string): boolean {
  try {
    const target = new URL(targetUrl);
    const app = new URL(appUrl);
    if (app.protocol === 'file:') return target.protocol === 'file:' && target.pathname === app.pathname;
    return target.origin === app.origin;
  } catch {
    return false;
  }
}

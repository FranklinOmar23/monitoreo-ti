/**
 * Enlaces profundos (sección 12.2): monitoreoti://maquina/{id}, monitoreoti://q/{codigo} y
 * monitoreoti://restablecer?token=…. Llegan de fuera (un correo, otra app, la línea de comandos): todo lo que no sea
 * exactamente uno de estos tres se descarta.
 */

export const PROTOCOL = 'monitoreoti';

export type DeepLink =
  { kind: 'maquina'; id: number } | { kind: 'qr'; code: string } | { kind: 'restablecer'; token: string };

const MAX_LENGTH = 2048;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const TOKEN = /^[A-Za-z0-9_-]{16,200}$/;

export function parseDeepLink(value: unknown): DeepLink | null {
  if (typeof value !== 'string' || value.length > MAX_LENGTH || !value.toLowerCase().startsWith(`${PROTOCOL}://`))
    return null;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  // Windows a veces agrega una barra al final al pasar el enlace.
  const path = url.pathname.replace(/^\/+|\/+$/g, '');
  switch (url.hostname.toLowerCase()) {
    case 'maquina': {
      if (!/^\d{1,9}$/.test(path)) return null;
      const id = Number(path);
      return id > 0 ? { kind: 'maquina', id } : null;
    }
    case 'q':
      return GUID.test(path) ? { kind: 'qr', code: path.toLowerCase() } : null;
    case 'restablecer': {
      const token = url.searchParams.get('token');
      return path === '' && token && TOKEN.test(token) ? { kind: 'restablecer', token } : null;
    }
    default:
      return null;
  }
}

/** El enlace entre los argumentos con que Windows abrió el panel (o una segunda instancia). */
export function findDeepLink(argv: readonly string[]): DeepLink | null {
  for (const arg of argv) {
    const link = parseDeepLink(arg);
    if (link) return link;
  }
  return null;
}

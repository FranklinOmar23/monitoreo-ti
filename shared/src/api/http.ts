/**
 * Base HTTP común a los clientes del API (panel y app móvil). Sin dependencias ni tipos del navegador:
 * recibe la URL del servidor y, si hace falta, la implementación de fetch.
 */

/** Error del API con el código estable que manda el servidor (credenciales_invalidas, cuenta_bloqueada…). */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly codigo: string | undefined,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** Lo mínimo de fetch que usan los clientes. */
export interface ResponseLike {
  ok: boolean;
  status: number;
  json(): Promise<unknown>;
}

export type FetchLike = (
  url: string,
  init: { method: string; headers: Record<string, string>; body?: string },
) => Promise<ResponseLike>;

const globalFetch: FetchLike = (url, init) => (globalThis as unknown as { fetch: FetchLike }).fetch(url, init);

export interface RequestOptions {
  method?: string;
  body?: unknown;
  accessToken?: string | null;
  query?: Record<string, string | number | null | undefined>;
}

export type HttpRequest = <T>(path: string, options?: RequestOptions) => Promise<T>;

export function createHttp(serverUrl: string, fetchImpl: FetchLike = globalFetch): HttpRequest {
  const base = serverUrl.replace(/\/+$/, '');

  return async <T>(path: string, options: RequestOptions = {}): Promise<T> => {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (options.body !== undefined) headers['Content-Type'] = 'application/json';
    if (options.accessToken) headers['Authorization'] = `Bearer ${options.accessToken}`;

    const query = Object.entries(options.query ?? {})
      .filter(([, value]) => value !== undefined && value !== null && value !== '')
      .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`)
      .join('&');

    let response: ResponseLike;
    try {
      response = await fetchImpl(`${base}${path}${query ? `?${query}` : ''}`, {
        method: options.method ?? (options.body === undefined ? 'GET' : 'POST'),
        headers,
        body: options.body === undefined ? undefined : JSON.stringify(options.body),
      });
    } catch {
      throw new ApiError(0, 'sin_conexion', 'No se pudo conectar con el servidor. Revise la red e intente de nuevo.');
    }

    if (!response.ok) throw await toApiError(response);
    if (response.status === 202 || response.status === 204) return undefined as T;
    return (await response.json()) as T;
  };
}

async function toApiError(response: ResponseLike): Promise<ApiError> {
  let body: { title?: string; codigo?: string; errors?: Record<string, string[]> } = {};
  try {
    body = (await response.json()) as typeof body;
  } catch {
    // Respuesta sin JSON (proxy, error del servidor): se usa el mensaje genérico.
  }

  if (response.status === 429) {
    return new ApiError(429, 'demasiadas_solicitudes', 'Demasiados intentos seguidos. Espere un minuto.');
  }
  if (response.status === 403) {
    return new ApiError(403, body.codigo ?? 'sin_permiso', body.title ?? 'No tiene permiso para esta acción.');
  }
  if (response.status === 404) {
    return new ApiError(404, body.codigo ?? 'no_encontrado', body.title ?? 'No se encontró lo que busca.');
  }

  const validation = body.errors ? Object.values(body.errors).flat()[0] : undefined;
  const message =
    validation ??
    body.title ??
    (response.status >= 500 ? 'El servidor tuvo un problema. Intente de nuevo.' : 'No se pudo completar la operación.');
  return new ApiError(response.status, body.codigo, message);
}

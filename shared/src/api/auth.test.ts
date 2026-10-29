import { describe, expect, it } from 'vitest';
import { createAuthApi } from './auth';
import { ApiError, type ResponseLike } from './http';

const respond = (status: number, body?: unknown): Promise<ResponseLike> =>
  Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => (body === undefined ? Promise.reject(new Error('sin cuerpo')) : Promise.resolve(body)),
  });

describe('createAuthApi', () => {
  it('envía el login como JSON a /api/v1/auth/login', async () => {
    let request: { url: string; init?: { method: string; body?: string } } | undefined;
    const api = createAuthApi('http://localhost:5126', (url, init) => {
      request = { url, init };
      return respond(200, { paso: 'completado', desafio: null, sesion: null, empresas: null, activacion2fa: null });
    });

    const result = await api.login('admin@caribe.test', 'clave');

    expect(request?.url).toBe('http://localhost:5126/api/v1/auth/login');
    expect(request?.init?.method).toBe('POST');
    expect(JSON.parse(String(request?.init?.body))).toEqual({ correo: 'admin@caribe.test', contrasena: 'clave' });
    expect(result.paso).toBe('completado');
  });

  it('manda el token de acceso en la cabecera Authorization', async () => {
    let headers: Record<string, string> | undefined;
    const api = createAuthApi('http://x', (_url, init) => {
      headers = init.headers;
      return respond(200, { usuario: {}, empresa: null, permisos: [], empresas: [] });
    });

    await api.session('abc');

    expect(headers?.['Authorization']).toBe('Bearer abc');
  });

  it('convierte los errores del servidor en ApiError con su código', async () => {
    const api = createAuthApi('http://x', () =>
      respond(423, { title: 'Demasiados intentos.', codigo: 'cuenta_bloqueada' }),
    );

    const error = await api.login('a@b.c', 'x').catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 423, codigo: 'cuenta_bloqueada', message: 'Demasiados intentos.' });
  });

  it('usa el primer error de validación como mensaje', async () => {
    const api = createAuthApi('http://x', () =>
      respond(400, {
        title: 'One or more validation errors occurred.',
        errors: { Codigo: ['El código tiene 6 dígitos.'] },
      }),
    );

    await expect(api.verifyTwoFactor('d', '1')).rejects.toThrow('El código tiene 6 dígitos.');
  });

  it('distingue la falta de conexión y el límite de intentos', async () => {
    const offline = createAuthApi('http://x', () => Promise.reject(new TypeError('fetch failed')));
    const limited = createAuthApi('http://x', () => respond(429));

    await expect(offline.login('a@b.c', 'x')).rejects.toMatchObject({ status: 0, codigo: 'sin_conexion' });
    await expect(limited.login('a@b.c', 'x')).rejects.toMatchObject({ status: 429, codigo: 'demasiadas_solicitudes' });
  });

  it('las respuestas sin cuerpo (202, 204) no fallan', async () => {
    const api = createAuthApi('http://x', () => respond(202));

    await expect(api.requestPasswordReset('a@b.c')).resolves.toBeUndefined();
  });
});

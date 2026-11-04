import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { ApiError, type AuthApi, type CurrentSession, type SessionTokens } from '@monitoreoti/shared';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MonitoreoBridge } from '../../../preload/bridge';
import { SessionProvider, useSession } from './SessionProvider';

const session: CurrentSession = {
  usuario: { id: 1, nombre: 'Ana Martínez', correo: 'admin@caribe.test', esSuperadmin: false },
  empresa: { id: 1, nombre: 'Grupo Comercial Caribe', rol: 'Administrador' },
  permisos: [],
  empresas: [],
};

const tokens = (refresh: string, minutes = 15): SessionTokens => ({
  accessToken: `access-${refresh}`,
  accessTokenExpira: new Date(Date.now() + minutes * 60_000).toISOString(),
  refreshToken: refresh,
  refreshTokenExpira: new Date(Date.now() + 86_400_000).toISOString(),
});

function Probe() {
  const { state, logout } = useSession();
  return (
    <div>
      <span data-testid="status">{state.status}</span>
      {state.status === 'authenticated' && <span>{state.session.usuario.nombre}</span>}
      {state.status === 'anonymous' && state.notice && <span>{state.notice}</span>}
      <button type="button" onClick={() => void logout()}>
        salir
      </button>
    </div>
  );
}

function installBridge(stored: string | null) {
  const store = { value: stored };
  const bridge: MonitoreoBridge = {
    getAppInfo: () => Promise.resolve({ name: 'x', version: '0', serverUrl: 'http://localhost:5126' }),
    saveFile: () => Promise.resolve({ saved: false }),
    copyText: () => Promise.resolve(),
    notify: () => Promise.resolve(),
    onNotificationClick: () => () => undefined,
    openTerminal: () => Promise.resolve(),
    terminalTicket: () => Promise.resolve(null),
    readText: () => Promise.resolve(''),
    requestSession: () => Promise.resolve(false),
    onSessionRequest: () => () => undefined,
    // El explorador de archivos no se usa en estas pruebas.
    files: {} as never,
    view3d: {} as never,
    deepLinks: { take: () => Promise.resolve(null), onOpen: () => () => undefined },
    tray: { setCriticalCount: () => Promise.resolve(), onNavigate: () => () => undefined },
    updates: { status: () => Promise.resolve(null), onReady: () => () => undefined, install: () => Promise.resolve() },
    session: {
      load: () => Promise.resolve(store.value),
      save: (token) => {
        store.value = token;
        return Promise.resolve();
      },
      clear: () => {
        store.value = null;
        return Promise.resolve();
      },
    },
  };
  window.monitoreo = bridge;
  return store;
}

function fakeApi(overrides: Partial<AuthApi>): AuthApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    login: missing,
    verifyTwoFactor: missing,
    confirmTwoFactorSetup: missing,
    selectCompany: missing,
    refresh: missing,
    logout: () => Promise.resolve(),
    session: () => Promise.resolve(session),
    switchCompany: missing,
    requestPasswordReset: missing,
    resetPassword: missing,
    ...overrides,
  };
}

describe('SessionProvider', () => {
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    delete window.monitoreo;
  });

  it('sin sesión guardada muestra el acceso', async () => {
    installBridge(null);
    render(
      <SessionProvider api={fakeApi({})}>
        <Probe />
      </SessionProvider>,
    );

    await waitFor(() => expect(screen.getByTestId('status').textContent).toBe('anonymous'));
  });

  it('con un refresh guardado entra sin contraseña y guarda el refresh nuevo', async () => {
    const store = installBridge('guardado');
    const refresh = vi.fn((token: string) => Promise.resolve(tokens(`${token}-2`)));
    render(
      <SessionProvider api={fakeApi({ refresh })}>
        <Probe />
      </SessionProvider>,
    );

    expect(await screen.findByText('Ana Martínez')).toBeTruthy();
    expect(refresh).toHaveBeenCalledWith('guardado');
    expect(store.value).toBe('guardado-2');
  });

  it('renueva el token de acceso un minuto antes de que venza', async () => {
    installBridge('r1');
    const refresh = vi.fn((token: string) => Promise.resolve(tokens(token === 'r1' ? 'r2' : 'r3', 5)));
    render(
      <SessionProvider api={fakeApi({ refresh })}>
        <Probe />
      </SessionProvider>,
    );
    await screen.findByText('Ana Martínez');

    await act(() => vi.advanceTimersByTimeAsync(4 * 60_000 + 1_000));

    expect(refresh).toHaveBeenLastCalledWith('r2');
  });

  it('si el refresh guardado ya no sirve, lo borra y pide iniciar sesión', async () => {
    const store = installBridge('viejo');
    render(
      <SessionProvider
        api={fakeApi({ refresh: () => Promise.reject(new ApiError(401, 'sesion_vencida', 'La sesión venció.')) })}
      >
        <Probe />
      </SessionProvider>,
    );

    await waitFor(() => expect(screen.getByTestId('status').textContent).toBe('anonymous'));
    expect(store.value).toBeNull();
  });

  it('si el servidor no responde al abrir, conserva la sesión guardada y reintenta', async () => {
    const store = installBridge('guardado');
    let online = false;
    const refresh = vi.fn(() =>
      online
        ? Promise.resolve(tokens('nuevo'))
        : Promise.reject(new ApiError(0, 'sin_conexion', 'No se pudo conectar con el servidor.')),
    );
    render(
      <SessionProvider api={fakeApi({ refresh })}>
        <Probe />
      </SessionProvider>,
    );

    await waitFor(() => expect(refresh).toHaveBeenCalledTimes(1));
    expect(screen.getByTestId('status').textContent).toBe('loading');
    expect(store.value).toBe('guardado');

    online = true;
    await act(() => vi.advanceTimersByTimeAsync(10_000));

    expect(await screen.findByText('Ana Martínez')).toBeTruthy();
  });

  it('un corte del servidor durante una renovación no cierra la sesión', async () => {
    installBridge('r1');
    let calls = 0;
    const refresh = vi.fn(() => {
      calls++;
      if (calls === 1) return Promise.resolve(tokens('r2', 2));
      if (calls === 2) return Promise.reject(new ApiError(503, undefined, 'El servidor tuvo un problema.'));
      return Promise.resolve(tokens('r3'));
    });
    render(
      <SessionProvider api={fakeApi({ refresh })}>
        <Probe />
      </SessionProvider>,
    );
    await screen.findByText('Ana Martínez');

    await act(() => vi.advanceTimersByTimeAsync(60_000 + 1_000)); // renovación programada: falla
    expect(screen.getByTestId('status').textContent).toBe('authenticated');

    await act(() => vi.advanceTimersByTimeAsync(10_000)); // reintento: funciona
    expect(refresh).toHaveBeenCalledTimes(3);
    expect(screen.getByTestId('status').textContent).toBe('authenticated');
  });

  it('cerrar sesión revoca el refresh en el servidor y lo borra del equipo', async () => {
    const store = installBridge('r1');
    const logout = vi.fn(() => Promise.resolve());
    render(
      <SessionProvider api={fakeApi({ refresh: () => Promise.resolve(tokens('r2')), logout })}>
        <Probe />
      </SessionProvider>,
    );
    await screen.findByText('Ana Martínez');

    await act(async () => screen.getByRole('button', { name: 'salir' }).click());

    await waitFor(() => expect(screen.getByTestId('status').textContent).toBe('anonymous'));
    expect(logout).toHaveBeenCalledWith('r2');
    expect(store.value).toBeNull();
  });
});

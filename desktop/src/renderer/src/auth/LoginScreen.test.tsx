import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { ApiError, type AuthApi, type LoginResponse, type SessionTokens } from '@monitoreoti/shared';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { receiveDeepLinkForTest } from '../data/deepLinks';
import { extractResetToken, LoginScreen } from './LoginScreen';

afterEach(cleanup);

const tokens: SessionTokens = {
  accessToken: 'a',
  accessTokenExpira: new Date(Date.now() + 900_000).toISOString(),
  refreshToken: 'r',
  refreshTokenExpira: new Date(Date.now() + 86_400_000).toISOString(),
};

const step = (paso: LoginResponse['paso'], extra: Partial<LoginResponse> = {}): LoginResponse => ({
  paso,
  desafio: 'desafio-1',
  sesion: null,
  empresas: null,
  activacion2fa: null,
  ...extra,
});

/** API falsa: cada método falla salvo los que la prueba defina. */
function fakeApi(overrides: Partial<AuthApi>): AuthApi {
  const missing = () => Promise.reject(new Error('no esperado'));
  return {
    login: missing,
    verifyTwoFactor: missing,
    confirmTwoFactorSetup: missing,
    selectCompany: missing,
    refresh: missing,
    logout: missing,
    session: missing,
    switchCompany: missing,
    requestPasswordReset: missing,
    resetPassword: missing,
    ...overrides,
  };
}

const typeCredentials = () => {
  fireEvent.change(screen.getByLabelText('Correo'), { target: { value: ' admin@caribe.test ' } });
  fireEvent.change(screen.getByLabelText('Contraseña'), { target: { value: 'Secreta123' } });
  fireEvent.click(screen.getByRole('button', { name: 'Entrar' }));
};

describe('LoginScreen', () => {
  it('con un solo paso entrega la sesión', async () => {
    const login = vi.fn(() => Promise.resolve(step('completado', { sesion: tokens })));
    const onSession = vi.fn(() => Promise.resolve());
    render(<LoginScreen api={fakeApi({ login })} onSession={onSession} />);

    typeCredentials();

    await waitFor(() => expect(onSession).toHaveBeenCalledWith(tokens));
    expect(login).toHaveBeenCalledWith('admin@caribe.test', 'Secreta123');
  });

  it('muestra el mensaje del servidor si las credenciales son incorrectas', async () => {
    const login = () => Promise.reject(new ApiError(401, 'credenciales_invalidas', 'Correo o contraseña incorrectos.'));
    render(<LoginScreen api={fakeApi({ login })} onSession={vi.fn()} />);

    typeCredentials();

    expect((await screen.findByRole('alert')).textContent).toBe('Correo o contraseña incorrectos.');
  });

  it('pide el código 2FA y luego la empresa', async () => {
    const verifyTwoFactor = vi.fn(() =>
      Promise.resolve(
        step('elegir-empresa', {
          desafio: 'desafio-2',
          empresas: [
            { id: 1, nombre: 'Grupo Comercial Caribe', rol: 'Supervisor' },
            { id: 2, nombre: 'Clínica San Rafael', rol: 'Tecnico' },
          ],
        }),
      ),
    );
    const selectCompany = vi.fn(() => Promise.resolve(step('completado', { sesion: tokens })));
    const onSession = vi.fn(() => Promise.resolve());
    render(
      <LoginScreen
        api={fakeApi({ login: () => Promise.resolve(step('verificar-2fa')), verifyTwoFactor, selectCompany })}
        onSession={onSession}
      />,
    );

    typeCredentials();
    // El autocompletado del sistema mete el código en la primera casilla: se reparte y se envía solo.
    fireEvent.change(await screen.findByLabelText('Dígito 1 de 6'), { target: { value: '12a34 56' } });

    fireEvent.click(await screen.findByRole('button', { name: /Clínica San Rafael/ }));

    await waitFor(() => expect(onSession).toHaveBeenCalledWith(tokens));
    expect(verifyTwoFactor).toHaveBeenCalledWith('desafio-1', '123456');
    expect(selectCompany).toHaveBeenCalledWith('desafio-2', 2);
  });

  it('para activar 2FA muestra el QR y la clave para escribir a mano', async () => {
    const login = () =>
      Promise.resolve(
        step('activar-2fa', {
          activacion2fa: { secreto: 'ABCDEFGHIJKLMNOP', uri: 'otpauth://totp/x', qr: 'data:image/png;base64,AAAA' },
        }),
      );
    render(<LoginScreen api={fakeApi({ login })} onSession={vi.fn()} />);

    typeCredentials();

    expect((await screen.findByAltText('Código QR para la app autenticadora')).getAttribute('src')).toBe(
      'data:image/png;base64,AAAA',
    );
    expect(screen.getByText('ABCD EFGH IJKL MNOP')).toBeTruthy();
  });

  it('un código incorrecto sacude y borra las casillas para escribirlo de nuevo', async () => {
    const verifyTwoFactor = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(401, 'codigo_invalido', 'El código no es válido.'))
      .mockResolvedValueOnce(step('completado', { sesion: tokens }));
    const onSession = vi.fn(() => Promise.resolve());
    render(
      <LoginScreen
        api={fakeApi({ login: () => Promise.resolve(step('verificar-2fa')), verifyTwoFactor })}
        onSession={onSession}
      />,
    );

    typeCredentials();
    fireEvent.change(await screen.findByLabelText('Dígito 1 de 6'), { target: { value: '111111' } });

    expect((await screen.findByRole('alert')).textContent).toBe('El código no es válido.');
    const first = screen.getByLabelText('Dígito 1 de 6') as HTMLInputElement;
    expect(first.value).toBe('');
    expect(first.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(first);

    fireEvent.change(first, { target: { value: '222222' } });
    await waitFor(() => expect(onSession).toHaveBeenCalledWith(tokens));
    expect(verifyTwoFactor).toHaveBeenLastCalledWith('desafio-1', '222222');
  });

  it('si el desafío venció vuelve al formulario de inicio', async () => {
    render(
      <LoginScreen
        api={fakeApi({
          login: () => Promise.resolve(step('verificar-2fa')),
          verifyTwoFactor: () => Promise.reject(new ApiError(401, 'desafio_invalido', 'La verificación venció.')),
        })}
        onSession={vi.fn()}
      />,
    );

    typeCredentials();
    fireEvent.change(await screen.findByLabelText('Dígito 1 de 6'), { target: { value: '123456' } });

    expect(await screen.findByRole('button', { name: 'Entrar' })).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toBe('La verificación venció.');
  });

  it('recuperar contraseña: pide el enlace y permite crear la contraseña nueva', async () => {
    const requestPasswordReset = vi.fn(() => Promise.resolve());
    const resetPassword = vi.fn(() => Promise.resolve());
    render(<LoginScreen api={fakeApi({ requestPasswordReset, resetPassword })} onSession={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: '¿Olvidó su contraseña?' }));
    fireEvent.change(screen.getByLabelText('Correo'), { target: { value: 'ana@caribe.test' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enviar enlace' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Ya tengo el enlace' }));

    fireEvent.change(screen.getByLabelText('Enlace del correo'), {
      target: { value: 'monitoreoti://restablecer?token=abc_DEF-123' },
    });
    fireEvent.change(screen.getByLabelText('Contraseña nueva'), { target: { value: 'NuevaClave2026' } });
    fireEvent.change(screen.getByLabelText('Repita la contraseña'), { target: { value: 'NuevaClave2026' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cambiar contraseña' }));

    expect((await screen.findByRole('status')).textContent).toMatch(/Contraseña cambiada/);
    expect(requestPasswordReset).toHaveBeenCalledWith('ana@caribe.test');
    expect(resetPassword).toHaveBeenCalledWith('abc_DEF-123', 'NuevaClave2026');
  });

  it('extrae el token del enlace o acepta el token solo', () => {
    expect(extractResetToken(' monitoreoti://restablecer?token=XyZ_1-2 ')).toBe('XyZ_1-2');
    expect(extractResetToken('XyZ_1-2')).toBe('XyZ_1-2');
  });
});

describe('enlace del correo (monitoreoti://restablecer)', () => {
  it('abre "Contraseña nueva" con el enlace ya puesto', async () => {
    render(<LoginScreen api={fakeApi({})} onSession={vi.fn()} />);
    act(() => receiveDeepLinkForTest({ kind: 'restablecer', token: 'abc_DEF-1234567890xyz' }));
    const link = (await screen.findByLabelText('Enlace del correo')) as HTMLInputElement;
    expect(link.value).toBe('monitoreoti://restablecer?token=abc_DEF-1234567890xyz');
    expect(extractResetToken(link.value)).toBe('abc_DEF-1234567890xyz');
  });
});

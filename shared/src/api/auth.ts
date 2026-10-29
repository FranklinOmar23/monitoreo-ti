/**
 * Cliente de /api/v1/auth, común al panel y a la app móvil. Sin dependencias: recibe la URL del
 * servidor y, si hace falta, la implementación de fetch.
 */

import { createHttp, type FetchLike, type RequestOptions } from './http';

export interface SessionTokens {
  accessToken: string;
  /** ISO 8601 en UTC. */
  accessTokenExpira: string;
  refreshToken: string;
  refreshTokenExpira: string;
}

export interface CompanyOption {
  id: number;
  nombre: string;
  rol: string;
}

export interface TwoFactorSetup {
  /** Secreto en Base32, para escribirlo a mano si la cámara no lee el QR. */
  secreto: string;
  uri: string;
  /** PNG del QR como data URI. */
  qr: string;
}

export type LoginStep = 'completado' | 'verificar-2fa' | 'activar-2fa' | 'elegir-empresa';

export interface LoginResponse {
  paso: LoginStep;
  desafio: string | null;
  sesion: SessionTokens | null;
  empresas: CompanyOption[] | null;
  activacion2fa: TwoFactorSetup | null;
}

export interface CurrentSession {
  usuario: { id: number; nombre: string; correo: string; esSuperadmin: boolean };
  empresa: CompanyOption | null;
  permisos: string[];
  empresas: CompanyOption[];
}

export interface AuthApi {
  login(correo: string, contrasena: string): Promise<LoginResponse>;
  verifyTwoFactor(desafio: string, codigo: string): Promise<LoginResponse>;
  confirmTwoFactorSetup(desafio: string, codigo: string): Promise<LoginResponse>;
  selectCompany(desafio: string, empresaId: number): Promise<LoginResponse>;
  refresh(refreshToken: string): Promise<SessionTokens>;
  logout(refreshToken: string): Promise<void>;
  session(accessToken: string): Promise<CurrentSession>;
  switchCompany(accessToken: string, empresaId: number): Promise<SessionTokens>;
  requestPasswordReset(correo: string): Promise<void>;
  resetPassword(token: string, nuevaContrasena: string): Promise<void>;
}

export function createAuthApi(serverUrl: string, fetchImpl?: FetchLike): AuthApi {
  const http = createHttp(serverUrl, fetchImpl);
  const call = <T>(path: string, options?: RequestOptions) => http<T>(`/api/v1/auth/${path}`, options);

  return {
    login: (correo, contrasena) => call('login', { body: { correo, contrasena } }),
    verifyTwoFactor: (desafio, codigo) => call('verificar-2fa', { body: { desafio, codigo } }),
    confirmTwoFactorSetup: (desafio, codigo) => call('activar-2fa', { body: { desafio, codigo } }),
    selectCompany: (desafio, empresaId) => call('seleccionar-empresa', { body: { desafio, empresaId } }),
    refresh: (refreshToken) => call('refresh', { body: { refreshToken } }),
    logout: (refreshToken) => call('logout', { body: { refreshToken } }),
    session: (accessToken) => call('sesion', { accessToken }),
    switchCompany: (accessToken, empresaId) => call('cambiar-empresa', { body: { empresaId }, accessToken }),
    requestPasswordReset: (correo) => call('recuperar', { body: { correo } }),
    resetPassword: (token, nuevaContrasena) => call('restablecer', { body: { token, nuevaContrasena } }),
  };
}

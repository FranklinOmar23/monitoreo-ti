/** Usuarios (W-36, W-37), roles y permisos (W-38) y Mi perfil (W-41). */
import type { ListParams, Paged } from './paging';

export interface BranchRef {
  id: number;
  name: string;
}

export interface UserItem {
  id: number;
  name: string;
  email: string;
  roleId: number;
  roleName: string;
  /** Vacío = ve todas las sucursales. */
  branches: BranchRef[];
  isActive: boolean;
  twoFactorEnabled: boolean;
  /** Su rol da acceso remoto: debe activar el 2FA al iniciar sesión. */
  twoFactorRequired: boolean;
  lastLoginAt: string | null;
  createdAt: string;
  /** Nunca inició sesión (todavía no creó su contraseña). */
  pending: boolean;
  lockedOut: boolean;
  /** También pertenece a otra empresa: su nombre, correo, contraseña y 2FA solo los cambia él. */
  shared: boolean;
  isSelf: boolean;
}

export interface UserInput {
  nombre: string;
  correo: string;
  rolId: number;
  sucursalIds?: number[];
}

/** link solo llega si el servidor no envía correos de verdad: hay que entregarlo a mano. */
export interface PasswordLink {
  userId: number;
  emailed: boolean;
  link: string | null;
  expiresAt: string;
}

export interface RoleItem {
  id: number;
  name: string;
  description: string | null;
  /** Rol del sistema: se ve y se duplica, no se cambia. */
  isSystem: boolean;
  permissions: string[];
  userCount: number;
}

export interface RoleInput {
  nombre: string;
  descripcion?: string | null;
  permisos: string[];
}

export interface PermissionItem {
  key: string;
  description: string;
  group: string;
  requiresTwoFactor: boolean;
}

export interface ProfileData {
  id: number;
  name: string;
  email: string;
  twoFactorEnabled: boolean;
  lastTwoFactorAt: string | null;
  notifyByEmail: boolean;
  createdAt: string;
}

export interface OpenSessionItem {
  id: number;
  startedAt: string;
  expiresAt: string;
  ip: string | null;
  device: string | null;
  current: boolean;
}

export interface UsersApi {
  list(params?: ListParams): Promise<Paged<UserItem>>;
  /** enlace es null si el correo ya tenía cuenta en otra empresa (entra con su contraseña de siempre). */
  create(input: UserInput): Promise<{ id: number; enlace: PasswordLink | null }>;
  update(id: number, input: UserInput): Promise<{ id: number }>;
  setActive(id: number, activo: boolean): Promise<{ id: number }>;
  passwordLink(id: number): Promise<PasswordLink>;
  resetTwoFactor(id: number): Promise<{ id: number }>;
  roles(): Promise<RoleItem[]>;
  permissions(): Promise<PermissionItem[]>;
  saveRole(id: number | null, input: RoleInput): Promise<{ id: number }>;
  deleteRole(id: number): Promise<{ id: number }>;
  profile(): Promise<ProfileData>;
  rename(nombre: string): Promise<{ id: number }>;
  /** Cierra las demás sesiones; la actual sigue abierta. */
  changePassword(contrasenaActual: string, nuevaContrasena: string): Promise<{ id: number }>;
  sessions(params?: ListParams): Promise<Paged<OpenSessionItem>>;
  closeSession(id: number): Promise<{ id: number }>;
  closeOtherSessions(): Promise<{ cerradas: number }>;
}

export function createUsersApi(
  get: <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) => Promise<T>,
  send: <T>(method: string, path: string, body?: unknown) => Promise<T>,
): UsersApi {
  return {
    list: (params = {}) => get('/usuarios', { ...params }),
    create: (input) => send('POST', '/usuarios', input),
    update: (id, input) => send('PUT', `/usuarios/${id}`, input),
    setActive: (id, activo) => send('POST', `/usuarios/${id}/${activo ? 'activar' : 'desactivar'}`),
    passwordLink: (id) => send('POST', `/usuarios/${id}/enlace-contrasena`),
    resetTwoFactor: (id) => send('POST', `/usuarios/${id}/reiniciar-2fa`),
    roles: () => get('/roles'),
    permissions: () => get('/permisos'),
    saveRole: (id, input) => (id === null ? send('POST', '/roles', input) : send('PUT', `/roles/${id}`, input)),
    deleteRole: (id) => send('DELETE', `/roles/${id}`),
    profile: () => get('/auth/perfil'),
    rename: (nombre) => send('PUT', '/auth/perfil', { nombre }),
    changePassword: (contrasenaActual, nuevaContrasena) =>
      send('POST', '/auth/contrasena', { contrasenaActual, nuevaContrasena }),
    sessions: (params = {}) => get('/auth/sesiones', { ...params }),
    closeSession: (id) => send('DELETE', `/auth/sesiones/${id}`),
    closeOtherSessions: () => send('POST', '/auth/sesiones/cerrar-otras'),
  };
}

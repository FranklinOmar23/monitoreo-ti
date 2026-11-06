import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { PermissionItem, RoleItem, UserItem } from '@monitoreoti/shared';
import { counts, fakePanelApi, fakeUsersApi, renderWithPanel, pageFrom } from '../../test/fakes';
import { PerfilPage } from '../PerfilPage';
import { UsuariosPage } from './UsuariosPage';

afterEach(cleanup);

const roles: RoleItem[] = [
  {
    id: 1,
    name: 'Administrador',
    description: 'Acceso total',
    isSystem: true,
    permissions: ['equipos.ver', 'usuarios.administrar'],
    userCount: 1,
  },
  {
    id: 2,
    name: 'Tecnico',
    description: null,
    isSystem: true,
    permissions: ['equipos.ver', 'remoto.conectar'],
    userCount: 0,
  },
  { id: 9, name: 'Mesa de ayuda', description: null, isSystem: false, permissions: ['tickets.crear'], userCount: 2 },
];

const permissions: PermissionItem[] = [
  { key: 'equipos.ver', description: 'Ver máquinas', group: 'Equipos', requiresTwoFactor: false },
  { key: 'tickets.crear', description: 'Crear tickets', group: 'Alertas y tickets', requiresTwoFactor: false },
  {
    key: 'remoto.conectar',
    description: 'Conectarse al escritorio remoto',
    group: 'Acceso remoto',
    requiresTwoFactor: true,
  },
  {
    key: 'usuarios.administrar',
    description: 'Administrar usuarios',
    group: 'Administración',
    requiresTwoFactor: false,
  },
];

const user = (overrides: Partial<UserItem>): UserItem => ({
  id: 5,
  name: 'Luis Peña',
  email: 'luis@caribe.test',
  roleId: 2,
  roleName: 'Tecnico',
  branches: [],
  isActive: true,
  twoFactorEnabled: false,
  twoFactorRequired: true,
  lastLoginAt: null,
  createdAt: '2026-10-01T12:00:00Z',
  pending: true,
  lockedOut: false,
  shared: false,
  isSelf: false,
  ...overrides,
});

const branches = [
  {
    id: 1,
    name: 'Santo Domingo',
    province: null,
    address: null,
    managerId: null,
    managerName: null,
    isActive: true,
    counts: counts(),
  },
  {
    id: 2,
    name: 'Santiago',
    province: null,
    address: null,
    managerId: null,
    managerName: null,
    isActive: true,
    counts: counts(),
  },
];

describe('Usuarios (W-36, W-37)', () => {
  it('lista los usuarios con su estado y filtra por texto', async () => {
    const list = () =>
      Promise.resolve([
        user({}),
        user({
          id: 6,
          name: 'Ana Martínez',
          email: 'admin@caribe.test',
          roleName: 'Administrador',
          pending: false,
          twoFactorEnabled: true,
          isSelf: true,
        }),
      ]);
    renderWithPanel(<UsuariosPage />, fakePanelApi({ usuarios: fakeUsersApi({ list: pageFrom(list) }) }));

    const row = (await screen.findByText('Luis Peña')).closest('tr')!;
    expect(within(row).getByText('Sin contraseña')).toBeTruthy();
    expect(within(row).getByText('Pendiente')).toBeTruthy();
    expect(within(row).getByText('Todas')).toBeTruthy();
    expect(screen.getByText('(usted)')).toBeTruthy();

    // El servidor filtra (decisión 83): la búsqueda viaja como q.
    fireEvent.change(screen.getByLabelText('Buscar por nombre o correo'), { target: { value: 'admin@' } });
    await waitFor(() => expect(screen.queryByText('Luis Peña')).toBeNull());
  });

  it('crea el usuario con sus sucursales y muestra el enlace para entregarlo a mano', async () => {
    const create = vi.fn(() =>
      Promise.resolve({
        id: 7,
        enlace: {
          userId: 7,
          emailed: false,
          link: 'monitoreoti://restablecer?token=abc',
          expiresAt: '2026-11-04T12:00:00Z',
        },
      }),
    );
    renderWithPanel(
      <UsuariosPage />,
      fakePanelApi({
        branches: () => Promise.resolve(branches),
        usuarios: fakeUsersApi({ roles: () => Promise.resolve(roles), create }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Agregar usuario/ }));
    fireEvent.change(screen.getByLabelText('Nombre *'), { target: { value: ' Rosa Díaz ' } });
    fireEvent.change(screen.getByLabelText('Correo *'), { target: { value: 'rosa@caribe.test' } });
    await within(await screen.findByRole('dialog')).findByRole('option', { name: 'Tecnico' });
    fireEvent.change(screen.getByLabelText('Rol *'), { target: { value: '2' } });
    expect(screen.getByText(/Da acceso remoto/)).toBeTruthy();
    fireEvent.click(await screen.findByLabelText('Santiago'));
    fireEvent.click(screen.getByRole('button', { name: 'Crear y enviar enlace' }));

    await waitFor(() =>
      expect(create).toHaveBeenCalledWith({
        nombre: 'Rosa Díaz',
        correo: 'rosa@caribe.test',
        rolId: 2,
        sucursalIds: [2],
      }),
    );
    expect(await screen.findByText('monitoreoti://restablecer?token=abc')).toBeTruthy();
    expect(screen.getByText(/no tiene correo configurado/)).toBeTruthy();
  });

  it('el Administrador ve todas las sucursales: no se marcan', async () => {
    const create = vi.fn(() => Promise.resolve({ id: 8, enlace: null }));
    renderWithPanel(
      <UsuariosPage />,
      fakePanelApi({
        branches: () => Promise.resolve(branches),
        usuarios: fakeUsersApi({ roles: () => Promise.resolve(roles), create }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Agregar usuario/ }));
    fireEvent.change(screen.getByLabelText('Nombre *'), { target: { value: 'Pedro' } });
    fireEvent.change(screen.getByLabelText('Correo *'), { target: { value: 'pedro@otra.test' } });
    await within(await screen.findByRole('dialog')).findByRole('option', { name: 'Administrador' });
    fireEvent.change(screen.getByLabelText('Rol *'), { target: { value: '1' } });
    expect(screen.getByText('El Administrador ve todas las sucursales.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Crear y enviar enlace' }));

    await waitFor(() => expect(create).toHaveBeenCalledWith(expect.objectContaining({ rolId: 1, sucursalIds: [] })));
    expect(await screen.findByText(/ya tenía cuenta en otra empresa/)).toBeTruthy();
  });

  it('de un usuario compartido con otra empresa solo se cambian el rol y las sucursales', async () => {
    const list = () => Promise.resolve([user({ shared: true, pending: false })]);
    renderWithPanel(
      <UsuariosPage />,
      fakePanelApi({ usuarios: fakeUsersApi({ list: pageFrom(list), roles: () => Promise.resolve(roles) }) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Luis Peña/ }));
    expect(screen.getByText(/también pertenece a otra empresa/)).toBeTruthy();
    expect((screen.getByLabelText('Nombre *') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('Correo *') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Restablecer contraseña' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('desactivar pide confirmación y usa la API', async () => {
    const setActive = vi.fn(() => Promise.resolve({ id: 5 }));
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderWithPanel(
      <UsuariosPage />,
      fakePanelApi({
        usuarios: fakeUsersApi({
          list: pageFrom(() => Promise.resolve([user({})])),
          roles: () => Promise.resolve(roles),
          setActive,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Luis Peña/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Desactivar' }));

    await waitFor(() => expect(setActive).toHaveBeenCalledWith(5, false));
    expect(confirm).toHaveBeenCalled();
    confirm.mockRestore();
  });
});

describe('Roles y permisos (W-38)', () => {
  it('muestra la matriz y un rol del sistema solo se duplica', async () => {
    const saveRole = vi.fn(() => Promise.resolve({ id: 10 }));
    renderWithPanel(
      <UsuariosPage />,
      fakePanelApi({
        usuarios: fakeUsersApi({
          roles: () => Promise.resolve(roles),
          permissions: () => Promise.resolve(permissions),
          saveRole,
        }),
      }),
    );

    fireEvent.click(await screen.findByRole('tab', { name: 'Roles y permisos' }));
    expect(await screen.findByRole('img', { name: 'Tecnico: Conectarse al escritorio remoto' })).toBeTruthy();
    expect(screen.getByRole('img', { name: 'Mesa de ayuda: sin Conectarse al escritorio remoto' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: /^Tecnico/ }));
    expect(screen.getByText(/Rol del sistema: no se cambia/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Duplicar' }));

    expect(((await screen.findByLabelText('Nombre *')) as HTMLInputElement).value).toBe('Tecnico (copia)');
    expect(screen.getByText(/sus usuarios deberán activar la verificación/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));

    await waitFor(() =>
      expect(saveRole).toHaveBeenCalledWith(null, {
        nombre: 'Tecnico (copia)',
        descripcion: null,
        permisos: ['equipos.ver', 'remoto.conectar'],
      }),
    );
  });
});

describe('Mi perfil (W-41)', () => {
  const profile = () =>
    Promise.resolve({
      id: 6,
      name: 'Ana Martínez',
      email: 'admin@caribe.test',
      twoFactorEnabled: true,
      lastTwoFactorAt: null,
      notifyByEmail: true,
      createdAt: '2026-01-01T00:00:00Z',
    });

  it('revisa la contraseña nueva antes de enviarla y luego la cambia', async () => {
    const changePassword = vi.fn(() => Promise.resolve({ id: 6 }));
    renderWithPanel(
      <PerfilPage companyName="Caribe Tours" roleName="Administrador" />,
      fakePanelApi({ usuarios: fakeUsersApi({ profile, changePassword }) }),
    );

    fireEvent.change(await screen.findByLabelText('Contraseña actual'), { target: { value: 'Vieja2026xx' } });
    fireEvent.change(screen.getByLabelText(/^Contraseña nueva/), { target: { value: 'NuevaClave2026' } });
    fireEvent.change(screen.getByLabelText('Repetir la contraseña nueva'), { target: { value: 'OtraClave2026' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cambiar contraseña' }));
    expect(await screen.findByText('Las contraseñas nuevas no coinciden.')).toBeTruthy();
    expect(changePassword).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('Repetir la contraseña nueva'), { target: { value: 'NuevaClave2026' } });
    fireEvent.click(screen.getByRole('button', { name: 'Cambiar contraseña' }));
    await waitFor(() => expect(changePassword).toHaveBeenCalledWith('Vieja2026xx', 'NuevaClave2026'));
    expect(await screen.findByText(/Se cerraron sus otras sesiones/)).toBeTruthy();
  });

  it('lista las sesiones abiertas y cierra las de otros equipos', async () => {
    const closeSession = vi.fn(() => Promise.resolve({ id: 2 }));
    const sessions = () =>
      Promise.resolve([
        {
          id: 1,
          startedAt: '2026-10-31T10:00:00Z',
          expiresAt: '2026-11-07T10:00:00Z',
          ip: '192.168.3.115',
          device: 'Centinela en PC-042076 (Windows)',
          current: true,
        },
        {
          id: 2,
          startedAt: '2026-10-30T10:00:00Z',
          expiresAt: '2026-11-06T10:00:00Z',
          ip: '192.168.3.20',
          device: 'Centinela en CASA (Windows)',
          current: false,
        },
      ]);
    renderWithPanel(
      <PerfilPage companyName="Caribe Tours" roleName="Administrador" />,
      fakePanelApi({ usuarios: fakeUsersApi({ profile, sessions: pageFrom(sessions), closeSession }) }),
    );

    const current = (await screen.findByText(/PC-042076/)).closest('tr')!;
    expect(within(current).getByText('Esta sesión')).toBeTruthy();
    expect(within(current).queryByRole('button', { name: 'Cerrar' })).toBeNull();

    const other = screen.getByText(/CASA/).closest('tr')!;
    fireEvent.click(within(other).getByRole('button', { name: 'Cerrar' }));
    await waitFor(() => expect(closeSession).toHaveBeenCalledWith(2));
    expect(screen.getByRole('button', { name: /Cerrar las otras 1 sesión/ })).toBeTruthy();
  });
});

import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type BranchItem, type DepartmentItem } from '@monitoreoti/shared';
import { counts, fakePanelApi, machineDetail, renderWithPanel } from '../test/fakes';
import { MachineForm } from './MachineForm';
import { SucursalesPage } from './SucursalesPage';

afterEach(cleanup);

const santiago: BranchItem = {
  id: 2,
  name: 'Santiago',
  province: 'Santiago',
  address: null,
  managerId: null,
  managerName: null,
  isActive: true,
  counts: counts(),
};

const dep = (overrides: Partial<DepartmentItem>): DepartmentItem => ({
  id: 10,
  name: 'Caja',
  machineCount: 0,
  branchId: 2,
  branchName: 'Santiago',
  responsibleId: null,
  responsibleName: null,
  ...overrides,
});

describe('Departamentos por sucursal (decisión 79)', () => {
  it('la sucursal lista sus departamentos y abre los equipos de uno', async () => {
    const onOpenMachines = vi.fn();
    renderWithPanel(
      <SucursalesPage canEdit onOpenMachines={onOpenMachines} />,
      fakePanelApi({
        branches: () => Promise.resolve([santiago]),
        branchDepartments: () =>
          Promise.resolve([
            dep({ id: 10, name: 'Caja', machineCount: 3, responsibleName: 'José Almonte' }),
            dep({ id: 11, name: 'Archivo' }),
          ]),
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Santiago/ }));
    const card = (await screen.findByRole('heading', { name: 'Departamentos' })).closest('section')!;
    const caja = (await within(card).findByText('Caja')).closest('tr')!;
    expect(within(caja).getByText('José Almonte')).toBeTruthy();
    // Con equipos no se elimina; vacío sí.
    expect((within(caja).getByRole('button', { name: 'Eliminar' }) as HTMLButtonElement).disabled).toBe(true);
    const archivo = within(card).getByText('Archivo').closest('tr')!;
    expect((within(archivo).getByRole('button', { name: 'Eliminar' }) as HTMLButtonElement).disabled).toBe(false);

    fireEvent.click(within(caja).getByRole('button', { name: '3' }));
    expect(onOpenMachines).toHaveBeenCalledWith(2, 10);
  });

  it('crea un departamento con responsable en la sucursal y muestra si el nombre ya existe', async () => {
    const saveDepartment = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(409, undefined, 'Esa sucursal ya tiene un departamento con ese nombre.'))
      .mockResolvedValue({ id: 12 });
    renderWithPanel(
      <SucursalesPage canEdit onOpenMachines={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([santiago]),
        branchDepartments: () => Promise.resolve([]),
        companyUsers: () => Promise.resolve([{ id: 5, nombre: 'Ana Martínez' }]),
        saveDepartment,
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Santiago/ }));
    fireEvent.click(await screen.findByRole('button', { name: /Agregar departamento/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Nombre *'), { target: { value: ' Contabilidad ' } });
    await within(dialog).findByRole('option', { name: 'Ana Martínez' });
    fireEvent.change(within(dialog).getByLabelText('Responsable'), { target: { value: '5' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Guardar' }));

    expect((await within(dialog).findByRole('alert')).textContent).toContain('ya tiene un departamento');
    expect(saveDepartment).toHaveBeenCalledWith(2, null, { nombre: 'Contabilidad', responsableId: 5 });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Guardar' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('sin permiso de edición se ven pero no se cambian', async () => {
    renderWithPanel(
      <SucursalesPage canEdit={false} onOpenMachines={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([santiago]),
        branchDepartments: () => Promise.resolve([dep({})]),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /Santiago/ }));
    expect(await screen.findByText('Caja')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Agregar departamento/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Eliminar' })).toBeNull();
  });

  it('el equipo solo ofrece los departamentos de su sucursal y los limpia al cambiarla', async () => {
    const departments = vi.fn((branchId?: number | null) =>
      Promise.resolve(
        branchId === 1
          ? [{ id: 1, nombre: 'Contabilidad', sucursalId: 1 }]
          : [{ id: 10, nombre: 'Caja', sucursalId: 2 }],
      ),
    );
    const updateMachine = vi.fn(() => Promise.resolve({ id: 7 }));
    renderWithPanel(
      <MachineForm machine={machineDetail({ branchId: 1, departmentId: 1 })} onClose={vi.fn()} onSaved={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([{ ...santiago, id: 1, name: 'Santo Domingo' }, santiago]),
        departments,
        updateMachine,
      }),
    );

    const select = screen.getByLabelText('Departamento') as HTMLSelectElement;
    await screen.findByRole('option', { name: 'Contabilidad' });
    expect(select.value).toBe('1');
    expect(screen.queryByRole('option', { name: 'Caja' })).toBeNull();

    fireEvent.change(screen.getByLabelText('Sucursal *'), { target: { value: '2' } });
    await screen.findByRole('option', { name: 'Caja' });
    expect(select.value).toBe('');
    expect(departments).toHaveBeenCalledWith(2);
  });
});

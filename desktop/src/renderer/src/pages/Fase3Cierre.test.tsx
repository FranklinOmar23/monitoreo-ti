import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { MaintenanceItem } from '@monitoreoti/shared';
import { Topbar } from '@components/Topbar';
import { counts, fakePanelApi, machineDetail, renderWithPanel } from '../test/fakes';
import { CalendarioPage, isoDate, monthGrid } from './CalendarioPage';
import { FichaPage } from './FichaPage';
import { MachineForm } from './MachineForm';

afterEach(cleanup);

const today = new Date();
const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1);

const maintenance = (overrides: Partial<MaintenanceItem>): MaintenanceItem => ({
  id: 5,
  machineId: 3,
  machineName: 'CONTA-03',
  machineType: 'computadora',
  branchId: 1,
  branchName: 'Santo Domingo',
  type: 'preventivo',
  description: 'Limpieza interna',
  scheduledDate: isoDate(today),
  completedDate: null,
  technicianId: null,
  technicianName: null,
  status: 'programado',
  ...overrides,
});

describe('calendario de mantenimiento (W-27)', () => {
  it('arma las semanas de lunes a domingo', () => {
    const days = monthGrid(2026, 9); // octubre de 2026: empieza en jueves
    expect(days.length % 7).toBe(0);
    expect(isoDate(days[0]!)).toBe('2026-09-28');
    expect(days[0]!.getDay()).toBe(1);
  });

  it('muestra los vencidos y marca uno como hecho con su nota', async () => {
    const completeMaintenance = vi.fn(() => Promise.resolve({ id: 5 }));
    renderWithPanel(
      <CalendarioPage canEdit onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        maintenances: () =>
          Promise.resolve({
            items: [maintenance({ scheduledDate: isoDate(yesterday), status: 'vencido' })],
            overdue: 1,
          }),
        completeMaintenance,
      }),
    );

    expect(await screen.findByText('1 mantenimiento vencido')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /CONTA-03/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox'), { target: { value: 'Se cambió la pasta térmica' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Marcar como hecho' }));

    await waitFor(() => expect(completeMaintenance).toHaveBeenCalledWith(5, null, 'Se cambió la pasta térmica'));
  });

  it('sin permiso de gestión no se programa ni se marca', async () => {
    renderWithPanel(
      <CalendarioPage canEdit={false} onOpenEquipo={vi.fn()} />,
      fakePanelApi({ maintenances: () => Promise.resolve({ items: [maintenance({})], overdue: 0 }) }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /CONTA-03/ }));
    expect(screen.queryByRole('button', { name: 'Marcar como hecho' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Programar mantenimiento/ })).toBeNull();
  });
});

describe('impresoras con SNMP', () => {
  it('el formulario de una impresora pide la comunidad v2c y el recolector', async () => {
    const createMachine = vi.fn(() => Promise.resolve({ id: 9 }));
    renderWithPanel(
      <MachineForm onClose={vi.fn()} onSaved={vi.fn()} />,
      fakePanelApi({
        createMachine,
        branches: () =>
          Promise.resolve([
            {
              id: 2,
              name: 'Santiago',
              province: null,
              address: null,
              managerId: null,
              managerName: null,
              isActive: true,
              counts: counts(0, 0, 0, 0),
            },
          ]),
        red: {
          ...fakePanelApi({}).red,
          collectors: () =>
            Promise.resolve([
              {
                agentId: 4,
                machineId: 7,
                machineName: 'CONTA-01',
                branchId: 2,
                ipAddress: null,
                online: true,
                lastContactAt: null,
                deviceCount: 0,
              },
            ]),
        },
      }),
    );

    fireEvent.change(screen.getByLabelText('Nombre *'), { target: { value: 'IMP-RRHH' } });
    fireEvent.change(screen.getByLabelText('Tipo *'), { target: { value: 'impresora' } });
    await screen.findByRole('option', { name: 'Santiago' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Sucursal *' }), { target: { value: '2' } });
    fireEvent.change(screen.getByLabelText('IP'), { target: { value: '10.30.20.90' } });
    fireEvent.change(screen.getByLabelText('Versión'), { target: { value: 'v2c' } });
    fireEvent.change(screen.getByLabelText('Comunidad'), { target: { value: 'lectura-123' } });
    await screen.findByRole('option', { name: 'CONTA-01' });
    fireEvent.change(screen.getByLabelText(/Recolector/), { target: { value: '4' } });
    fireEvent.submit(document.getElementById('machine-form')!);

    await waitFor(() =>
      expect(createMachine).toHaveBeenCalledWith(
        expect.objectContaining({
          tipo: 'impresora',
          snmpVersion: 'v2c',
          snmpComunidad: 'lectura-123',
          recolectorId: 4,
          snmpClaveAutenticacion: null,
        }),
      ),
    );
  });

  it('la ficha de la impresora muestra cuánto le queda a cada consumible', async () => {
    const printer = machineDetail({
      id: 9,
      name: 'IMP-RRHH',
      type: 'impresora',
      hasAgent: false,
      components: [
        {
          id: 1,
          type: 'toner',
          description: 'Tóner negro',
          capacity: '3 %',
          health: 'critico',
          detail: '3 de 100',
          updatedAt: today.toISOString(),
        },
      ],
      problems: [],
      snmp: {
        version: 'v2c',
        user: null,
        hasCredential: true,
        collectorId: 4,
        collectorName: 'CONTA-01',
        lastPollAt: today.toISOString(),
        lastError: null,
      },
    });
    renderWithPanel(
      <FichaPage machineId={9} onBack={vi.fn()} />,
      fakePanelApi({ machine: () => Promise.resolve(printer) }),
    );

    expect(await screen.findByText('Tóner negro · 3 %')).toBeTruthy();
    expect(screen.getByText('Cambiar')).toBeTruthy();
    expect(screen.getByText(/lo consulta CONTA-01/)).toBeTruthy();
  });
});

describe('notificaciones', () => {
  it('la campana lista las alertas abiertas y abre la elegida', () => {
    const onOpen = vi.fn();
    render(
      <Topbar
        search=""
        onSearchChange={vi.fn()}
        user={{ nombre: 'Ana Martínez', rol: 'Administrador' }}
        hasUnreadAlerts
        menu={[]}
        notifications={[
          { id: 41, level: 'critico', message: 'Disco C: al 97 %', where: 'CONTA-03', at: today.toISOString() },
        ]}
        onOpenNotification={onOpen}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Notificaciones (hay nuevas)' }));
    fireEvent.click(
      within(screen.getByRole('dialog', { name: 'Notificaciones' })).getByRole('button', { name: /Disco C: al 97 %/ }),
    );
    expect(onOpen).toHaveBeenCalledWith(41);
    expect(screen.queryByRole('dialog', { name: 'Notificaciones' })).toBeNull();
  });
});

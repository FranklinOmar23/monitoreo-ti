import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ScheduledTaskItem, ScriptItem, UpdateStatus } from '@monitoreoti/shared';
import { fakePanelApi, fakeRemoteApi, renderWithPanel } from '../../test/fakes';
import { AgentUpdates } from '../AgentUpdates';
import { RemotoPage } from './RemotoPage';

afterEach(cleanup);

const script: ScriptItem = {
  id: 4,
  name: 'Limpiar temporales',
  language: 'powershell',
  description: null,
  content: 'Remove-Item $env:TEMP\\* -Recurse',
  createdBy: 'Ana Martínez',
  createdAt: '2026-10-30T12:00:00Z',
  updatedAt: null,
  signed: true,
  lastRunAt: null,
};

const task: ScheduledTaskItem = {
  id: 1,
  name: 'Limpieza nocturna',
  scriptId: 4,
  scriptName: 'Limpiar temporales',
  target: 'todos',
  branchId: null,
  branchName: null,
  machineIds: [],
  frequency: 'semanal',
  time: '02:00',
  date: null,
  weekDays: 31,
  monthDay: null,
  timeoutSeconds: 300,
  active: true,
  nextRunAt: '2026-11-02T06:00:00Z',
  lastRunAt: null,
  lastBatchId: null,
  createdBy: 'Ana Martínez',
  description: 'De lunes a viernes a las 02:00',
};

describe('tareas programadas (6.5)', () => {
  it('lista las tareas con su descripción en palabras', async () => {
    renderWithPanel(
      <RemotoPage canRun canEdit={false} onOpenEquipo={vi.fn()} />,
      fakePanelApi({ remoto: fakeRemoteApi({ tasks: () => Promise.resolve([task]) }) }),
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Tareas programadas' }));
    expect(await screen.findByText('Limpieza nocturna')).toBeTruthy();
    expect(screen.getByText('De lunes a viernes a las 02:00')).toBeTruthy();
    expect(screen.getByText('Todos los equipos')).toBeTruthy();
  });

  it('programa un script cada semana en los días marcados', async () => {
    const saveTask = vi.fn(() => Promise.resolve({ id: 2 }));
    renderWithPanel(
      <RemotoPage canRun canEdit={false} onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([]),
        remoto: fakeRemoteApi({ saveTask, scripts: () => Promise.resolve([script]) }),
      }),
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Tareas programadas' }));
    fireEvent.click(await screen.findByRole('button', { name: /Nueva tarea/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Nombre *'), { target: { value: 'Limpieza' } });
    await within(dialog).findByRole('option', { name: 'Limpiar temporales' });
    fireEvent.change(within(dialog).getByLabelText('Script *'), { target: { value: '4' } });
    fireEvent.change(within(dialog).getByLabelText('Cuándo'), { target: { value: 'semanal' } });
    fireEvent.click(within(dialog).getByLabelText('Viernes')); // quita el viernes (por defecto de lunes a viernes)
    fireEvent.change(within(dialog).getByLabelText('Dónde'), { target: { value: 'todos' } });
    fireEvent.submit(document.getElementById('task-form')!);

    await waitFor(() =>
      expect(saveTask).toHaveBeenCalledWith(
        null,
        expect.objectContaining({
          nombre: 'Limpieza',
          scriptId: 4,
          frecuencia: 'semanal',
          diasSemana: 15,
          objetivo: 'todos',
          hora: '02:00',
        }),
      ),
    );
  });
});

describe('actualización del agente (6.6)', () => {
  const status: UpdateStatus = {
    available: '0.2.0',
    problem: null,
    autoUpdate: true,
    total: 3,
    upToDate: 1,
    outdated: 2,
    updating: 1,
    outdatedAgents: [
      { machineId: 3, machineName: 'CONTA-03', branchName: 'Santiago', version: '0.1.0', online: true, updating: true },
      {
        machineId: 5,
        machineName: 'CAJA-01',
        branchName: 'Santiago',
        version: '0.1.0',
        online: false,
        updating: false,
      },
    ],
  };

  it('muestra cuántos están al día y despliega a los conectados', async () => {
    const deployUpdate = vi.fn(() => Promise.resolve({ ofrecidos: 1 }));
    renderWithPanel(
      <AgentUpdates />,
      fakePanelApi({ remoto: fakeRemoteApi({ updates: () => Promise.resolve(status), deployUpdate }) }),
    );

    expect(await screen.findByText('1 de 3')).toBeTruthy();
    expect(screen.getByText('Actualizándose…')).toBeTruthy();
    expect(screen.getByText('Desconectado')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Actualizar ahora' }));
    expect(await screen.findByText(/Se ofreció la versión a 1 equipo/)).toBeTruthy();
  });

  it('explica por qué no hay versión para ofrecer', async () => {
    renderWithPanel(
      <AgentUpdates />,
      fakePanelApi({
        remoto: fakeRemoteApi({
          updates: () =>
            Promise.resolve({ ...status, available: null, problem: 'Falta la llave de firma de las actualizaciones.' }),
        }),
      }),
    );

    expect(await screen.findByRole('alert')).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Actualizar ahora' }) as HTMLButtonElement).disabled).toBe(true);
  });
});

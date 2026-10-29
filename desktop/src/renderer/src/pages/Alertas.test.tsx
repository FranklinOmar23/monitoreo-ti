import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AlertDetail, RuleItem } from '@monitoreoti/shared';
import { alertItem, alertList, fakePanelApi, renderWithPanel } from '../test/fakes';
import { AlertasPage } from './AlertasPage';

afterEach(cleanup);

const now = new Date().toISOString();

const detail: AlertDetail = {
  ...alertItem(),
  detail: 'Quedan 3 GB libres de 100 GB.',
  advice: 'Libera espacio (archivos temporales, descargas, papelera) o amplía el disco.',
  code: 'disco_lleno:C:',
  machineStatus: 'critico',
  ruleId: 3,
  ruleName: 'Disco usado',
  recurrences: 2,
  history: [{ at: now, action: 'Detectada por el sistema', userName: null, note: null }],
};

const diskRule: RuleItem = {
  id: 3,
  metric: 'disco',
  name: 'Disco usado',
  description: 'Espacio ocupado de cada unidad.',
  unit: '%',
  lessIsWorse: false,
  hasThresholds: true,
  hasDuration: false,
  available: true,
  warning: 80,
  critical: 95,
  durationMinutes: 0,
  isActive: true,
  overrides: [],
};

describe('Alertas (W-20 a W-23)', () => {
  it('lista las alertas abiertas, filtra con los contadores y abre el detalle', async () => {
    const alerts = vi.fn(() => Promise.resolve(alertList([alertItem()])));
    const alert = vi.fn(() => Promise.resolve(detail));
    renderWithPanel(<AlertasPage canManage onOpenEquipo={vi.fn()} />, fakePanelApi({ alerts, alert }));

    expect(await screen.findByText('Disco C: al 97 % de su capacidad')).toBeTruthy();
    fireEvent.click(screen.getByText('1 críticas abiertas'));
    await waitFor(() => expect(alerts).toHaveBeenLastCalledWith(expect.objectContaining({ nivel: 'critico' })));

    fireEvent.click(screen.getByRole('button', { name: 'Disco C: al 97 % de su capacidad' }));

    expect(await screen.findByText(/Libera espacio/)).toBeTruthy();
    expect(screen.getByText('2 veces antes')).toBeTruthy();
    expect(screen.getByText('Detectada por el sistema')).toBeTruthy();
  });

  it('resolver pide una nota y avisa que el motor la reabre si el problema sigue', async () => {
    const resolveAlert = vi.fn(() => Promise.resolve({ id: 41 }));
    renderWithPanel(
      <AlertasPage canManage onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        alerts: () => Promise.resolve(alertList([alertItem()])),
        alert: () => Promise.resolve(detail),
        resolveAlert,
      }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Disco C: al 97 % de su capacidad' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Resolver' }));
    expect(screen.getByText(/abrirá una alerta nueva/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText('Nota (opcional)'), { target: { value: 'Se vació la papelera' } });
    fireEvent.click(screen.getByRole('button', { name: 'Marcar como resuelta' }));

    await waitFor(() => expect(resolveAlert).toHaveBeenCalledWith(41, 'Se vació la papelera'));
  });

  it('sin permiso de gestión no muestra acciones', async () => {
    renderWithPanel(
      <AlertasPage canManage={false} onOpenEquipo={vi.fn()} />,
      fakePanelApi({ alerts: () => Promise.resolve(alertList([alertItem()])), alert: () => Promise.resolve(detail) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Disco C: al 97 % de su capacidad' }));
    await screen.findByText(/Libera espacio/);
    expect(screen.queryByRole('button', { name: 'Resolver' })).toBeNull();
  });

  it('ajusta una regla para una sucursal', async () => {
    const saveRule = vi.fn(() => Promise.resolve({ id: 9 }));
    renderWithPanel(
      <AlertasPage canManage onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        rules: () => Promise.resolve([diskRule]),
        saveRule,
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
              counts: { total: 0, healthy: 0, warning: 0, critical: 0, disconnected: 0, online: 0 },
            },
          ]),
      }),
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Reglas' }));
    expect(await screen.findByText('> 80 %')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Ajustar' }));
    fireEvent.change(await screen.findByLabelText('Aplicar a'), { target: { value: '2' } });
    await screen.findByRole('option', { name: 'Sucursal Santiago' });
    fireEvent.change(screen.getByLabelText(/Advertencia si es mayor que/), { target: { value: '70' } });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));

    await waitFor(() =>
      expect(saveRule).toHaveBeenCalledWith({
        metrica: 'disco',
        sucursalId: 2,
        umbralAdvertencia: 70,
        umbralCritico: 95,
        duracionMin: 0,
        activa: true,
      }),
    );
  });
});

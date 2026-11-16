import { act, cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakePanelApi, machineDetail, renderWithPanel } from '../test/fakes';
import { FichaPage } from './FichaPage';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('"Actualizar" en la ficha (decisión 85)', () => {
  it('pide al agente que reporte ya y vuelve a leer procesos y servicios', async () => {
    const reportNow = vi.fn(() => Promise.resolve({ enviado: true, segundos: 0 }));
    const processes = vi.fn(() => Promise.resolve([]));
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} />,
      fakePanelApi({ machine: () => Promise.resolve(machineDetail({ status: 'sano', hasAgent: true })), reportNow, processes }),
    );

    const button = await screen.findByRole('button', { name: 'Actualizar' });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const before = processes.mock.calls.length;
    fireEvent.click(button);
    expect(await screen.findByRole('button', { name: 'Actualizando…' })).toBeTruthy();
    expect(reportNow).toHaveBeenCalledWith(1);

    await act(() => vi.advanceTimersByTimeAsync(12_000));
    expect(processes.mock.calls.length).toBeGreaterThan(before);
    expect(screen.getByRole('button', { name: 'Actualizar' })).toBeTruthy();
  });

  it('explica cuando el agente no está conectado', async () => {
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} />,
      fakePanelApi({
        machine: () => Promise.resolve(machineDetail({ status: 'sano', hasAgent: true })),
        reportNow: () => Promise.reject(new Error('El agente no está conectado ahora: el equipo está apagado o sin red.')),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: 'Actualizar' }));
    expect((await screen.findByRole('status')).textContent).toContain('no está conectado');
  });

  it('sin agente no hay botón', async () => {
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} />,
      fakePanelApi({ machine: () => Promise.resolve(machineDetail({ status: 'sano', hasAgent: false })) }),
    );
    await screen.findByText(/Último reporte/);
    expect(screen.queryByRole('button', { name: 'Actualizar' })).toBeNull();
  });
});

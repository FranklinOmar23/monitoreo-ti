import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { MachineFilter, MachineList } from '@monitoreoti/shared';
import { fakePanelApi, machine, machineList, pageOf, renderWithPanel } from '../test/fakes';
import { EquiposPage } from './EquiposPage';

afterEach(cleanup);

/** 25 equipos: el servidor devuelve la página pedida, de 10 en 10. */
function server() {
  const all = Array.from({ length: 25 }, (_, i) => machine({ id: i + 1, name: `PC-${String(i + 1).padStart(2, '0')}` }));
  return vi.fn((filter: MachineFilter = {}): Promise<MachineList> => {
    const page = filter.page ?? 1;
    const limit = filter.limit ?? 10;
    return Promise.resolve({
      ...machineList([]),
      data: pageOf(all.slice((page - 1) * limit, page * limit), all.length, page, limit),
    });
  });
}

describe('paginación de los listados (decisión 83)', () => {
  it('muestra 10 por página, cuántas páginas hay y avanza con Siguiente', async () => {
    const machines = server();
    renderWithPanel(<EquiposPage query="" onQueryChange={vi.fn()} onOpenEquipo={vi.fn()} />, fakePanelApi({ machines }));

    expect(await screen.findByText('Mostrando 1–10 de 25 equipos')).toBeTruthy();
    expect(screen.getByText('Página 1 de 3')).toBeTruthy();
    expect((screen.getByRole('button', { name: '‹ Anterior' }) as HTMLButtonElement).disabled).toBe(true);
    expect(machines).toHaveBeenCalledWith(expect.objectContaining({ page: 1, limit: 10 }));

    fireEvent.click(screen.getByRole('button', { name: 'Siguiente ›' }));
    expect(await screen.findByText('Mostrando 11–20 de 25 equipos')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Siguiente ›' }));
    expect(await screen.findByText('Página 3 de 3')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'PC-25' })).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Siguiente ›' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('«Más filtros» manda los campos y el rango de fechas, y vuelve a la página 1', async () => {
    const machines = server();
    renderWithPanel(<EquiposPage query="" onQueryChange={vi.fn()} onOpenEquipo={vi.fn()} />, fakePanelApi({ machines }));
    fireEvent.click(await screen.findByRole('button', { name: 'Siguiente ›' }));
    await screen.findByText('Página 2 de 3');

    fireEvent.click(screen.getByRole('button', { name: 'Más filtros' }));
    fireEvent.change(screen.getByLabelText('Número de serie'), { target: { value: 'ABC' } });
    fireEvent.change(screen.getByLabelText('Alta desde'), { target: { value: '2026-10-01' } });
    await waitFor(() =>
      expect(machines).toHaveBeenLastCalledWith(
        expect.objectContaining({ serie: 'ABC', fechaDesde: '2026-10-01', page: 1, limit: 10 }),
      ),
    );
    expect(screen.getByRole('button', { name: 'Más filtros (2)' })).toBeTruthy();

    // Un rango al revés no se manda y se avisa.
    fireEvent.change(screen.getByLabelText('Alta hasta'), { target: { value: '2026-09-01' } });
    expect(await screen.findByText('La fecha «desde» es posterior a «hasta».')).toBeTruthy();
    await waitFor(() =>
      expect(machines).toHaveBeenLastCalledWith(expect.objectContaining({ fechaDesde: undefined, fechaHasta: undefined })),
    );
  });

  it('con una sola página no aparece el paginador', async () => {
    renderWithPanel(
      <EquiposPage query="" onQueryChange={vi.fn()} onOpenEquipo={vi.fn()} />,
      fakePanelApi({ machines: () => Promise.resolve(machineList([machine()])) }),
    );
    await screen.findByRole('table');
    expect(screen.queryByRole('navigation', { name: /Páginas de/ })).toBeNull();
  });
});

import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { machineLabel } from '../data/format';
import { fakePanelApi, machineDetail, renderWithPanel } from '../test/fakes';
import { FichaPage } from './FichaPage';
import { MachineForm } from './MachineForm';

afterEach(cleanup);

describe('alias de los equipos (decisión 87)', () => {
  it('la ficha muestra el alias grande y el nombre del equipo debajo', async () => {
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} />,
      fakePanelApi({
        machine: () => Promise.resolve(machineDetail({ name: 'PC-042076', alias: 'Caja 3 – Recepción', status: 'sano' })),
      }),
    );
    expect(await screen.findByRole('heading', { name: 'Caja 3 – Recepción' })).toBeTruthy();
    expect(screen.getAllByText('PC-042076').length).toBeGreaterThan(0);
  });

  it('el formulario guarda el alias, y vacío lo quita', async () => {
    const updateMachine = vi.fn(() => Promise.resolve({ id: 1 }));
    renderWithPanel(
      <MachineForm machine={machineDetail({ alias: 'Caja 3' })} onClose={vi.fn()} onSaved={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([{ id: 1, name: 'Santo Domingo' }] as never),
        updateMachine,
      }),
    );
    await screen.findByRole('option', { name: 'Santo Domingo' });
    const field = screen.getByLabelText(/Alias/) as HTMLInputElement;
    expect(field.value).toBe('Caja 3');

    fireEvent.change(field, { target: { value: '  Caja 4  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    await waitFor(() => expect(updateMachine).toHaveBeenCalledWith(1, expect.objectContaining({ alias: 'Caja 4' })));

    fireEvent.change(field, { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar' }));
    await waitFor(() => expect(updateMachine).toHaveBeenLastCalledWith(1, expect.objectContaining({ alias: null })));
  });

  it('el nombre para ventanas y tickets lleva el alias', () => {
    expect(machineLabel({ name: 'PC-042076', alias: 'Caja 3' })).toBe('Caja 3 (PC-042076)');
    expect(machineLabel({ name: 'PC-042076', alias: null })).toBe('PC-042076');
  });
});

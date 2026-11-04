import { cleanup, fireEvent, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakePanelApi, machineDetail, renderWithPanel } from '../test/fakes';
import { FichaPage } from './FichaPage';

afterEach(cleanup);

describe('acciones de la ficha', () => {
  it('las acciones poco frecuentes están en el menú Más', async () => {
    const onEdit = vi.fn();
    const onRetire = vi.fn();
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} onEdit={onEdit} onRetire={onRetire} />,
      fakePanelApi({ machine: () => Promise.resolve(machineDetail({ status: 'sano' })) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'Más' }));
    expect(screen.getByRole('menuitem', { name: 'Dar de baja' })).toBeTruthy();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Editar' }));

    expect(onEdit).toHaveBeenCalled();
    expect(screen.queryByRole('menu')).toBeNull();
  });
});

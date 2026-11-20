import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ErrorGroupItem, ErrorGroupList } from '@monitoreoti/shared';
import { fakePanelApi, fakeRemoteApi, machineDetail, renderWithPanel } from '../test/fakes';
import { ErrorsTab } from './ErrorsTab';

afterEach(cleanup);

function group(overrides: Partial<ErrorGroupItem> = {}): ErrorGroupItem {
  return {
    source: 'MSSQLSERVER',
    code: 701,
    level: 'error',
    count: 340,
    firstAt: '2026-11-18T10:00:00Z',
    lastAt: '2026-11-19T19:12:00Z',
    title: 'SQL Server se quedó sin memoria',
    detail: 'SQL Server no tiene memoria para atender consultas.',
    advice: 'Reiniciar el servicio libera la memoria ya.',
    lastMessage: "There is insufficient system memory in resource pool 'default' to run this query.",
    state: 'abierto',
    stateScope: null,
    stateBy: null,
    stateAt: null,
    stateNote: null,
    openCount: 340,
    service: 'MSSQLSERVER',
    noise: false,
    ...overrides,
  };
}

const list = (groups: ErrorGroupItem[]): ErrorGroupList => ({
  days: 7,
  openTotal: groups.reduce((n, g) => n + (g.state === 'abierto' || g.state === 'reaparecio' ? g.openCount : 0), 0),
  groups,
});

const props = { canManage: true, canFix: true, canIgnoreCompanyWide: true };

describe('Errores del equipo (decisión 91)', () => {
  it('agrupa los repetidos y oculta los resueltos hasta pedirlos', async () => {
    renderWithPanel(
      <ErrorsTab machine={machineDetail({ hasAgent: true })} {...props} />,
      fakePanelApi({
        errorGroups: () =>
          Promise.resolve(
            list([
              group(),
              group({ source: 'Disk', code: 7, title: 'Errores de lectura en el disco', state: 'resuelto', service: null }),
            ]),
          ),
      }),
    );
    expect(await screen.findByText('SQL Server se quedó sin memoria')).toBeTruthy();
    expect(screen.getByText('×340')).toBeTruthy();
    expect(screen.getByText(/340 errores abiertos/)).toBeTruthy();
    expect(screen.queryByText('Errores de lectura en el disco')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: /Resueltos/ }));
    expect(screen.getByText('Errores de lectura en el disco')).toBeTruthy();
  });

  it('marca un grupo resuelto con su nota', async () => {
    const markErrorGroup = vi.fn(() => Promise.resolve({ id: 1 }));
    renderWithPanel(
      <ErrorsTab machine={machineDetail({ id: 4, hasAgent: true })} {...props} />,
      fakePanelApi({ errorGroups: () => Promise.resolve(list([group()])), markErrorGroup }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /SQL Server se quedó sin memoria/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Marcar resuelto' }));
    fireEvent.change(screen.getByPlaceholderText(/Se limitó/), { target: { value: 'Memoria limitada a 12 GB' } });
    fireEvent.click(screen.getAllByRole('button', { name: 'Marcar resuelto' }).at(-1)!);
    await waitFor(() =>
      expect(markErrorGroup).toHaveBeenCalledWith(4, {
        origen: 'MSSQLSERVER',
        codigo: 701,
        estado: 'resuelto',
        alcance: 'equipo',
        nota: 'Memoria limitada a 12 GB',
      }),
    );
  });

  it('el ruido de Windows se propone ignorar en toda la empresa', async () => {
    const markErrorGroup = vi.fn(() => Promise.resolve({ id: 2 }));
    renderWithPanel(
      <ErrorsTab machine={machineDetail({ id: 4 })} {...props} />,
      fakePanelApi({
        errorGroups: () =>
          Promise.resolve(
            list([group({ source: 'DistributedCOM', code: 10016, title: 'Aviso de permisos DCOM', noise: true, service: null })]),
          ),
        markErrorGroup,
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /Aviso de permisos DCOM/ }));
    expect(screen.queryByRole('button', { name: /Reiniciar/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Ignorar \(ruido de Windows\)/ }));
    expect((screen.getByLabelText('En todos los equipos de la empresa') as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: 'Ignorar' }));
    await waitFor(() =>
      expect(markErrorGroup).toHaveBeenCalledWith(4, expect.objectContaining({ estado: 'ignorado', alcance: 'empresa' })),
    );
  });

  it('reinicia el servicio con un comando y muestra el resultado', async () => {
    const fixErrorGroup = vi.fn(() => Promise.resolve({ lote: 'b1', servicio: 'MSSQLSERVER' }));
    renderWithPanel(
      <ErrorsTab machine={machineDetail({ id: 4, hasAgent: true })} {...props} />,
      fakePanelApi({
        errorGroups: () => Promise.resolve(list([group()])),
        fixErrorGroup,
        remoto: fakeRemoteApi({
          runDetail: () =>
            Promise.resolve({
              commands: [{ status: 'completado', exitCode: 0, output: 'Running' }],
            } as never),
        }),
      }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /SQL Server se quedó sin memoria/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Reiniciar MSSQLSERVER' }));
    await waitFor(() => expect(fixErrorGroup).toHaveBeenCalledWith(4, { origen: 'MSSQLSERVER', codigo: 701 }));
    expect(await screen.findByRole('button', { name: 'MSSQLSERVER reiniciado' })).toBeTruthy();
  });

  it('sin permisos solo se ve el diagnóstico', async () => {
    renderWithPanel(
      <ErrorsTab machine={machineDetail({ id: 4 })} canManage={false} canFix={false} canIgnoreCompanyWide={false} />,
      fakePanelApi({ errorGroups: () => Promise.resolve(list([group()])) }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /SQL Server se quedó sin memoria/ }));
    expect(screen.getByText(/Reiniciar el servicio libera/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Reiniciar MSSQLSERVER/ })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Marcar resuelto' })).toBeNull();
  });
});

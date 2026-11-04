import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Shell } from './App';
import { receiveDeepLinkForTest } from './data/deepLinks';
import { DashboardPage } from './pages/DashboardPage';
import { EquiposPage } from './pages/EquiposPage';
import { FichaPage } from './pages/FichaPage';
import { dashboard, fakePanelApi, machine, machineDetail, machineList, renderWithPanel, session } from './test/fakes';

afterEach(cleanup);

describe('Panel con datos del API', () => {
  it('el dashboard muestra los totales del servidor', async () => {
    renderWithPanel(
      <DashboardPage onOpenEquipo={vi.fn()} onNavigate={vi.fn()} />,
      fakePanelApi({ dashboard: () => Promise.resolve(dashboard) }),
    );

    // 248 aparece en la tarjeta del total y en el eje de la gráfica.
    expect((await screen.findAllByText('248')).length).toBeGreaterThan(0);
    expect(screen.getByText('+6 registrados esta semana')).toBeTruthy();
    expect(screen.getByText('Disco con sectores dañados')).toBeTruthy();
  });

  it('la lista de equipos selecciona filas y muestra la barra de acciones en lote', async () => {
    renderWithPanel(
      <EquiposPage query="" onQueryChange={vi.fn()} onOpenEquipo={vi.fn()} onPrintQr={vi.fn()} />,
      fakePanelApi({
        machines: () => Promise.resolve(machineList([machine(), machine({ id: 2, name: 'CAJA-01', status: 'sano' })])),
      }),
    );

    fireEvent.click(await screen.findByLabelText('Seleccionar CONTA-03'));
    fireEvent.click(screen.getByLabelText('Seleccionar CAJA-01'));

    expect(screen.getByText('2 equipos seleccionados')).toBeTruthy();
    fireEvent.click(screen.getByText('Quitar selección'));
    expect(screen.queryByRole('toolbar')).toBeNull();
  });

  it('los chips filtran por estado en el servidor', async () => {
    const machines = vi.fn(() => Promise.resolve(machineList([machine()])));
    renderWithPanel(
      <EquiposPage query="" onQueryChange={vi.fn()} onOpenEquipo={vi.fn()} />,
      fakePanelApi({ machines }),
    );

    fireEvent.click(await screen.findByText('1 desconectados'));

    await waitFor(() => expect(machines).toHaveBeenLastCalledWith(expect.objectContaining({ estado: 'desconectado' })));
  });

  it('abre la ficha del equipo al hacer clic en su nombre', async () => {
    const onOpen = vi.fn();
    renderWithPanel(
      <EquiposPage query="" onQueryChange={vi.fn()} onOpenEquipo={onOpen} />,
      fakePanelApi({ machines: () => Promise.resolve(machineList([machine({ id: 7 })])) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: 'CONTA-03' }));

    expect(onOpen).toHaveBeenCalledWith(7);
  });

  it('la ficha muestra estado, métricas, problemas y carga cada pestaña bajo demanda', async () => {
    const software = vi.fn(() =>
      Promise.resolve([{ name: '7-Zip 24.08', version: '24.08', publisher: 'Igor Pavlov', installedOn: '2024-09-10' }]),
    );
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} />,
      fakePanelApi({ machine: () => Promise.resolve(machineDetail()), software }),
    );

    expect(await screen.findByText('Falla crítica')).toBeTruthy();
    expect(screen.getByText('92%')).toBeTruthy();
    expect(screen.getByText('Disco con sectores dañados')).toBeTruthy();
    expect(software).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('tab', { name: 'Software instalado' }));

    expect(await screen.findByText('7-Zip 24.08')).toBeTruthy();
  });

  it('la ficha explica cada problema, muestra los consumos y abre los servicios', async () => {
    const at = new Date().toISOString();
    const onCreateTicket = vi.fn();
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} onCreateTicket={onCreateTicket} />,
      fakePanelApi({
        machine: () => Promise.resolve(machineDetail()),
        processes: () => Promise.resolve([{ name: 'chrome', pid: 10, cpu: 12.5, ramMb: 800, at }]),
        services: () =>
          Promise.resolve([
            {
              name: 'Spooler',
              displayName: 'Cola de impresión',
              state: 'detenido',
              startMode: 'automatico',
              stoppedSince: at,
            },
          ]),
      }),
    );

    expect(await screen.findByText('Disco C:')).toBeTruthy();
    expect(screen.getByText('437 / 476 GB')).toBeTruthy();
    expect(screen.getByText('11.4 / 16 GB')).toBeTruthy();
    expect(screen.getByText('CPU paquete')).toBeTruthy();
    expect(screen.getByText(/Respalda los datos hoy/)).toBeTruthy();
    expect(screen.getByText(/SMART · 148 sectores dañados/)).toBeTruthy();
    expect(screen.getByText('Reemplazar')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Crear ticket/ }));
    expect(onCreateTicket).toHaveBeenCalledWith(expect.objectContaining({ id: 1 }));
    expect(await screen.findByText('chrome')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Ver todos (142)' }));

    expect(await screen.findByText('Cola de impresión')).toBeTruthy();
    expect(screen.getByText('1 caídos')).toBeTruthy();
  });

  it('un equipo desconectado no muestra métricas como si fueran actuales', async () => {
    renderWithPanel(
      <FichaPage machineId={1} onBack={vi.fn()} />,
      fakePanelApi({ machine: () => Promise.resolve(machineDetail({ status: 'desconectado', metrics: null })) }),
    );

    expect(await screen.findByText('El equipo está desconectado')).toBeTruthy();
  });

  it('la estructura muestra el usuario y la empresa de la sesión', async () => {
    renderWithPanel(
      <Shell session={session} menu={[]} />,
      fakePanelApi({ dashboard: () => Promise.resolve(dashboard) }),
    );

    expect(screen.getByText('Ana Martínez')).toBeTruthy();
    expect(screen.getAllByText('Grupo Comercial Caribe').length).toBeGreaterThan(0);
  });

  it('un enlace monitoreoti:// abre la ficha del equipo, también si llega por su código QR', async () => {
    const machineCall = vi.fn(() => Promise.resolve(machineDetail({ id: 3 })));
    const resolveQr = vi.fn(() => Promise.resolve({ type: 'maquina' as const, id: 3 }));
    renderWithPanel(
      <Shell session={session} menu={[]} />,
      fakePanelApi({ dashboard: () => Promise.resolve(dashboard), machine: machineCall, resolveQr }),
    );
    act(() => receiveDeepLinkForTest({ kind: 'maquina', id: 3 }));
    await waitFor(() => expect(machineCall).toHaveBeenCalledWith(3));

    act(() => receiveDeepLinkForTest({ kind: 'qr', code: '0f8fad5b-d9cb-469f-a165-70867728950e' }));
    await waitFor(() => expect(resolveQr).toHaveBeenCalledWith('0f8fad5b-d9cb-469f-a165-70867728950e'));
  });
});

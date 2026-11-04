import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type RunDetail, type ScriptItem } from '@monitoreoti/shared';
import { fakePanelApi, fakeRemoteApi, renderWithPanel } from '../../test/fakes';
import { RemotoPage } from './RemotoPage';
import { RunScriptDialog } from './RunScriptDialog';

afterEach(cleanup);

const script: ScriptItem = {
  id: 4,
  name: 'Limpiar temporales',
  language: 'powershell',
  description: 'Borra %TEMP%',
  content: 'Remove-Item $env:TEMP\\* -Recurse -Force',
  createdBy: 'Ana Martínez',
  createdAt: '2026-10-30T12:00:00Z',
  updatedAt: null,
  signed: true,
  lastRunAt: null,
};

describe('ejecutar script (W-32)', () => {
  it('pide el código 2FA cuando el servidor lo exige y reintenta al confirmarlo', async () => {
    const run = vi
      .fn()
      .mockRejectedValueOnce(new ApiError(403, '2fa_requerido', 'Confirme su código.'))
      .mockResolvedValueOnce({ batchId: 'lote-1', count: 1 });
    const confirmTwoFactor = vi.fn(() => Promise.resolve({ confirmado: '', vigenteHasta: '' }));
    const onStarted = vi.fn();
    renderWithPanel(
      <RunScriptDialog machines={[{ id: 3, name: 'CONTA-03' }]} onClose={vi.fn()} onStarted={onStarted} />,
      fakePanelApi({ remoto: fakeRemoteApi({ run, confirmTwoFactor }) }),
    );

    fireEvent.change(screen.getByLabelText('Comando'), { target: { value: 'ipconfig /all' } });
    fireEvent.change(screen.getByLabelText('Lenguaje'), { target: { value: 'cmd' } });
    fireEvent.click(screen.getByRole('button', { name: 'Ejecutar en 1 equipo' }));

    const prompt = await screen.findByRole('dialog', { name: 'Confirme su identidad' });
    fireEvent.change(within(prompt).getByLabelText('Dígito 1 de 6'), { target: { value: '123456' } });

    await waitFor(() => expect(onStarted).toHaveBeenCalledWith('lote-1'));
    expect(confirmTwoFactor).toHaveBeenCalledWith('123456');
    expect(run).toHaveBeenLastCalledWith({
      maquinaIds: [3],
      scriptId: null,
      lenguaje: 'cmd',
      contenido: 'ipconfig /all',
      timeoutS: 300,
    });
  });

  it('con un script de la biblioteca muestra su contenido y manda solo su id', async () => {
    const run = vi.fn(() => Promise.resolve({ batchId: 'lote-2', count: 2 }));
    renderWithPanel(
      <RunScriptDialog
        machines={[
          { id: 3, name: 'CONTA-03' },
          { id: 5, name: 'CAJA-01' },
        ]}
        scriptId={4}
        onClose={vi.fn()}
        onStarted={vi.fn()}
      />,
      fakePanelApi({ remoto: fakeRemoteApi({ run, scripts: () => Promise.resolve([script]) }) }),
    );

    expect(await screen.findByLabelText('Contenido del script')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Ejecutar en 2 equipos' }));
    await waitFor(() =>
      expect(run).toHaveBeenCalledWith(expect.objectContaining({ maquinaIds: [3, 5], scriptId: 4, contenido: null })),
    );
  });
});

describe('acceso remoto', () => {
  it('muestra el resultado de cada equipo de una ejecución', async () => {
    const detail: RunDetail = {
      summary: {
        batchId: 'lote-1',
        title: 'Limpiar temporales',
        language: 'powershell',
        userName: 'Luis Peña',
        createdAt: '2026-10-31T09:00:00Z',
        total: 2,
        pending: 0,
        running: 0,
        completed: 1,
        failed: 1,
      },
      content: script.content,
      timeoutSeconds: 300,
      commands: [
        {
          id: 1,
          batchId: 'lote-1',
          machineId: 3,
          machineName: 'CONTA-03',
          branchName: 'Santiago',
          status: 'completado',
          exitCode: 0,
          createdAt: '2026-10-31T09:00:00Z',
          sentAt: '2026-10-31T09:00:01Z',
          completedAt: '2026-10-31T09:00:05Z',
          output: 'Listo',
        },
        {
          id: 2,
          batchId: 'lote-1',
          machineId: 5,
          machineName: 'CAJA-01',
          branchName: 'Santiago',
          status: 'error',
          exitCode: null,
          createdAt: '2026-10-31T09:00:00Z',
          sentAt: '2026-10-31T09:00:01Z',
          completedAt: '2026-10-31T09:00:02Z',
          output: 'El agente lo rechazó: firma no válida',
        },
      ],
    };
    renderWithPanel(
      <RemotoPage canRun canEdit={false} initialBatchId="lote-1" onOpenEquipo={vi.fn()} />,
      fakePanelApi({ remoto: fakeRemoteApi({ runDetail: () => Promise.resolve(detail) }) }),
    );

    expect(await screen.findByText('Con error')).toBeTruthy();
    fireEvent.click(screen.getAllByRole('button', { name: 'Ver salida' })[1]!);
    expect(screen.getByLabelText('Salida de CAJA-01').textContent).toContain('firma no válida');
  });

  it('quien solo ejecuta ve la biblioteca sin poder editarla', async () => {
    renderWithPanel(
      <RemotoPage canRun canEdit={false} onOpenEquipo={vi.fn()} />,
      fakePanelApi({ remoto: fakeRemoteApi({ scripts: () => Promise.resolve([script]) }) }),
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Biblioteca de scripts' }));
    expect(await screen.findByText('Limpiar temporales')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Ejecutar' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Editar' })).toBeNull();
    expect(screen.queryByRole('button', { name: /Nuevo script/ })).toBeNull();
  });

  it('al guardar un script lo manda para firmarlo', async () => {
    const saveScript = vi.fn(() => Promise.resolve({ id: 9 }));
    renderWithPanel(
      <RemotoPage canRun={false} canEdit onOpenEquipo={vi.fn()} />,
      fakePanelApi({ remoto: fakeRemoteApi({ saveScript }) }),
    );

    fireEvent.click(await screen.findByRole('button', { name: /Nuevo script/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Nombre *'), { target: { value: 'Reiniciar cola' } });
    fireEvent.change(within(dialog).getByLabelText('Contenido *'), { target: { value: 'Restart-Service Spooler' } });
    fireEvent.submit(document.getElementById('script-form')!);

    await waitFor(() =>
      expect(saveScript).toHaveBeenCalledWith(null, {
        nombre: 'Reiniciar cola',
        lenguaje: 'powershell',
        descripcion: null,
        contenido: 'Restart-Service Spooler',
      }),
    );
  });
});

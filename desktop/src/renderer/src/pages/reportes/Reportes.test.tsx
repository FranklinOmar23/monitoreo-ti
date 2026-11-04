import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { InventoryReport } from '@monitoreoti/shared';
import { fakePanelApi, fakeReportsApi, renderWithPanel } from '../../test/fakes';
import { toCsv } from './csv';
import { periodRange, ReportesPage } from './ReportesPage';

afterEach(cleanup);

const inventory: InventoryReport = {
  total: 1,
  byType: [{ key: 'computadora', count: 1 }],
  byOperatingSystem: [{ key: 'Windows 11 Pro', count: 1 }],
  rows: [
    {
      id: 7,
      name: 'CONTA-03',
      type: 'computadora',
      branchName: 'Santiago',
      departmentName: 'Contabilidad',
      assignedTo: 'Ana Pérez',
      operatingSystem: 'Windows 11 Pro',
      processor: 'Intel Core i5-12400',
      ramGb: 16,
      disks: '512 GB SSD',
      brand: 'Dell',
      model: 'OptiPlex 7000',
      serialNumber: 'ABC123',
      ipAddress: '10.0.0.7',
      macAddress: 'AA-BB-CC-DD-EE-FF',
      status: 'sano',
      warrantyUntil: null,
      createdAt: '2026-10-01T12:00:00Z',
      lastReportAt: '2026-11-01T12:00:00Z',
    },
  ],
};

describe('Reportes (W-35)', () => {
  it('sin el módulo, los reportes avanzados quedan con candado', async () => {
    renderWithPanel(
      <ReportesPage onOpenEquipo={vi.fn()} />,
      fakePanelApi({ reportes: fakeReportsApi({ info: () => Promise.resolve({ advanced: false }) }) }),
    );
    const performance = (await screen.findByRole('button', { name: /Rendimiento/ })) as HTMLButtonElement;
    await waitFor(() => expect(performance.disabled).toBe(true));
    expect(screen.getAllByText('Disponible en el plan Empresarial')).toHaveLength(4);
    expect((screen.getByRole('button', { name: /Inventario de equipos/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('abre el inventario con sus filtros y un clic en el equipo abre su ficha', async () => {
    const load = vi.fn(() => Promise.resolve(inventory));
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <ReportesPage onOpenEquipo={onOpenEquipo} />,
      fakePanelApi({ branches: () => Promise.resolve([]), reportes: fakeReportsApi({ inventory: load }) }),
    );
    fireEvent.click(await screen.findByRole('button', { name: /Inventario de equipos/ }));

    expect(await screen.findByText('Intel Core i5-12400')).toBeTruthy();
    // El inventario es la foto de hoy: no tiene periodo.
    expect(screen.queryByRole('group', { name: 'Periodo' })).toBeNull();
    expect(load).toHaveBeenCalledWith(expect.not.objectContaining({ desde: expect.anything() }));

    fireEvent.click(screen.getByRole('button', { name: 'CONTA-03' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(7);
    expect((screen.getByRole('button', { name: /Exportar CSV/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('el periodo cambia las fechas que se piden', async () => {
    const alerts = vi.fn(() =>
      Promise.resolve({
        total: 0,
        critical: 0,
        warning: 0,
        open: 0,
        averageResolutionHours: null,
        perDay: [],
        byCode: [],
        byBranch: [],
        topMachines: [],
      }),
    );
    renderWithPanel(
      <ReportesPage onOpenEquipo={vi.fn()} initialReport="alertas" />,
      fakePanelApi({ branches: () => Promise.resolve([]), reportes: fakeReportsApi({ alerts }) }),
    );
    await waitFor(() => expect(alerts).toHaveBeenCalledWith(expect.objectContaining(periodRange('30'))));
    fireEvent.click(screen.getByRole('button', { name: '7 días' }));
    await waitFor(() => expect(alerts).toHaveBeenLastCalledWith(expect.objectContaining(periodRange('7'))));
  });

  it('el CSV abre bien en Excel: acentos, comillas y nada que parezca una fórmula', () => {
    const csv = toCsv({
      columns: ['Equipo', 'Nota'],
      rows: [
        ['PC "Ñ"', '=HYPERLINK("x")'],
        ['A,B', null],
      ],
    });
    expect(csv.startsWith('﻿')).toBe(true);
    expect(csv).toContain('"PC ""Ñ""","\'=HYPERLINK(""x"")"');
    expect(csv).toContain('"A,B",\r\n');
  });

  it('últimos 7 días con hoy incluido', () => {
    expect(periodRange('7', new Date(2026, 10, 1))).toEqual({ desde: '2026-10-26', hasta: '2026-11-01' });
  });
});

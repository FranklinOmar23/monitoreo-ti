import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AlertReport, InventoryReport, ReportSummary } from '@monitoreoti/shared';
import { fakePanelApi, fakeReportsApi, renderWithPanel } from '../../test/fakes';
import { toCsv } from './csv';
import { periodRange, ReportesPage } from './ReportesPage';
import { ReportTable } from './ReportTable';
import { deltaText, deltaTone } from './widgets';

afterEach(cleanup);

const row = (over: Partial<InventoryReport['rows'][number]> = {}): InventoryReport['rows'][number] => ({
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
  ...over,
});

const inventory: InventoryReport = {
  total: 1,
  byType: [{ key: 'computadora', count: 1 }],
  byOperatingSystem: [{ key: 'Windows 11 Pro', count: 1 }],
  rows: [row()],
};

const emptyAlerts: AlertReport = {
  total: 0,
  critical: 0,
  warning: 0,
  open: 0,
  averageResolutionHours: null,
  perDay: [],
  byCode: [],
  byBranch: [],
  topMachines: [],
};

const summary: ReportSummary = {
  previousFrom: '2026-09-24',
  previousTo: '2026-10-23',
  advanced: false,
  kpis: [
    { key: 'disponibilidad', value: 97.2, previous: 95.8, series: [96, 98, 97] },
    { key: 'alertas_criticas', value: 2, previous: 3, series: [1, 0, 1] },
    { key: 'discos_riesgo', value: 1, previous: null, series: [] },
  ],
  attention: [
    {
      code: 'sin_proteccion',
      level: 'critico',
      report: 'discos-seguridad',
      count: 2,
      machines: [{ id: 9, name: 'CAJA-01' }],
    },
  ],
  branches: [{ name: 'Santiago', machines: 4, availability: 97.2, alerts: 5 }],
};

describe('Reportes (W-35, decisión 92)', () => {
  it('abre en el Resumen: KPI con variación, lo que requiere atención y el reparto por sucursal', async () => {
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <ReportesPage onOpenEquipo={onOpenEquipo} />,
      fakePanelApi({
        branches: () => Promise.resolve([]),
        reportes: fakeReportsApi({
          info: () => Promise.resolve({ advanced: false }),
          summary: () => Promise.resolve(summary),
        }),
      }),
    );

    expect(await screen.findByText('2 equipos sin antivirus o con el firewall apagado')).toBeTruthy();
    // La disponibilidad subió 1.4 puntos: es buena noticia.
    const delta = screen.getByText('▲ 1.4 pts');
    expect(delta.getAttribute('data-tone')).toBe('sano');
    // Las alertas críticas bajaron: también.
    expect(screen.getByText('▼ 33%').getAttribute('data-tone')).toBe('sano');
    expect(screen.getByText(/disponible en el plan Empresarial/)).toBeTruthy();
    expect(within(screen.getByRole('region', { name: 'Por sucursal' })).getByText('Santiago')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'CAJA-01' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(9);
  });

  it('sin el módulo, los reportes avanzados quedan con candado', async () => {
    renderWithPanel(
      <ReportesPage onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([]),
        reportes: fakeReportsApi({
          info: () => Promise.resolve({ advanced: false }),
          summary: () => Promise.resolve(summary),
        }),
      }),
    );
    const nav = screen.getByRole('navigation', { name: 'Reportes' });
    await waitFor(() =>
      expect(within(nav).getByRole('button', { name: 'Rendimiento' }).hasAttribute('data-locked')).toBe(true),
    );
    expect(within(nav).getByRole('button', { name: 'Inventario' }).hasAttribute('data-locked')).toBe(false);

    fireEvent.click(within(nav).getByRole('button', { name: 'Rendimiento' }));
    expect(await screen.findByText(/es parte de los reportes avanzados/)).toBeTruthy();
  });

  it('el inventario es la foto de hoy; los filtros se conservan y un clic en el equipo abre su ficha', async () => {
    const load = vi.fn(() => Promise.resolve(inventory));
    const onOpenEquipo = vi.fn();
    renderWithPanel(
      <ReportesPage onOpenEquipo={onOpenEquipo} />,
      fakePanelApi({
        branches: () => Promise.resolve([{ id: 3, name: 'Santiago' }] as never),
        reportes: fakeReportsApi({ summary: () => Promise.resolve(summary), inventory: load }),
      }),
    );
    fireEvent.click(
      within(screen.getByRole('navigation', { name: 'Reportes' })).getByRole('button', { name: 'Inventario' }),
    );

    expect(await screen.findByText('Intel Core i5-12400')).toBeTruthy();
    expect(screen.queryByRole('group', { name: 'Periodo' })).toBeNull();
    expect(screen.getByText('FOTO DE HOY')).toBeTruthy();
    expect(load).toHaveBeenCalledWith(expect.not.objectContaining({ desde: expect.anything() }));

    fireEvent.click(screen.getByRole('button', { name: 'CONTA-03' }));
    expect(onOpenEquipo).toHaveBeenCalledWith(7);
    expect((screen.getByRole('button', { name: /Exportar CSV/ }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('al pasar de un reporte a otro no se mezclan sus datos', async () => {
    let finish: (value: AlertReport) => void = () => {};
    renderWithPanel(
      <ReportesPage onOpenEquipo={vi.fn()} />,
      fakePanelApi({
        branches: () => Promise.resolve([]),
        reportes: fakeReportsApi({
          summary: () => Promise.resolve(summary),
          inventory: () => Promise.resolve(inventory),
          alerts: () => new Promise<AlertReport>((resolve) => (finish = resolve)),
        }),
      }),
    );
    const nav = screen.getByRole('navigation', { name: 'Reportes' });
    fireEvent.click(within(nav).getByRole('button', { name: 'Inventario' }));
    expect(await screen.findByText('Intel Core i5-12400')).toBeTruthy();

    // Mientras cargan las alertas no se intenta dibujarlas con los datos del inventario.
    fireEvent.click(within(nav).getByRole('button', { name: /^Alertas/ }));
    expect(await screen.findByText('Preparando el reporte…')).toBeTruthy();
    finish(emptyAlerts);
    expect(await screen.findByText('No hubo alertas en el periodo.')).toBeTruthy();
  });

  it('el periodo cambia las fechas que se piden', async () => {
    const alerts = vi.fn(() => Promise.resolve(emptyAlerts));
    renderWithPanel(
      <ReportesPage onOpenEquipo={vi.fn()} initialReport="alertas" />,
      fakePanelApi({ branches: () => Promise.resolve([]), reportes: fakeReportsApi({ alerts }) }),
    );
    await waitFor(() => expect(alerts).toHaveBeenCalledWith(expect.objectContaining(periodRange('30'))));
    fireEvent.click(screen.getByRole('button', { name: '7 días' }));
    await waitFor(() => expect(alerts).toHaveBeenLastCalledWith(expect.objectContaining(periodRange('7'))));
    // Sin el resumen (falló), el reporte se ve igual, solo sin variación.
    expect(await screen.findByText('No hubo alertas en el periodo.')).toBeTruthy();
  });

  it('la tabla ordena por columna y filtra solo los problemas', () => {
    const rows = [
      row({ id: 1, name: 'B', ramGb: 8 }),
      row({ id: 2, name: 'A', ramGb: 32, status: 'critico' }),
      row({ id: 3, name: 'C', ramGb: null }),
    ];
    render(
      <ReportTable
        noun="equipos"
        empty="Nada"
        rows={rows}
        rowKey={(r) => r.id}
        problem={(r) => r.status !== 'sano'}
        columns={[
          { key: 'name', label: 'Equipo', sort: (r) => r.name, render: (r) => r.name },
          { key: 'ram', label: 'RAM', sort: (r) => r.ramGb, render: (r) => String(r.ramGb ?? '—') },
        ]}
      />,
    );
    const names = () =>
      screen
        .getAllByRole('row')
        .slice(1)
        .map((r) => r.firstChild?.textContent);

    fireEvent.click(screen.getByRole('button', { name: /Equipo/ }));
    expect(names()).toEqual(['A', 'B', 'C']);
    fireEvent.click(screen.getByRole('button', { name: /RAM/ }));
    fireEvent.click(screen.getByRole('button', { name: /RAM/ }));
    // Descendente, y lo vacío siempre al final.
    expect(names()).toEqual(['A', 'B', 'C']);
    expect(screen.getByRole('columnheader', { name: /RAM/ }).getAttribute('aria-sort')).toBe('descending');

    fireEvent.click(screen.getByRole('checkbox', { name: /Solo con problemas/ }));
    expect(names()).toEqual(['A']);
  });

  it('la variación se lee según el indicador', () => {
    expect(deltaText(97.2, 95.8, '%')).toEqual({ text: '▲ 1.4 pts', diff: 1.4 });
    expect(deltaText(2, 3).text).toBe('▼ 33%');
    expect(deltaText(4, 0).text).toBe('▲ 4');
    expect(deltaText(5, 5).text).toBe('igual');
    expect(deltaTone(1.4, 'up')).toBe('sano');
    expect(deltaTone(2, 'down')).toBe('critico');
    expect(deltaTone(2, 'none')).toBe('neutral');
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

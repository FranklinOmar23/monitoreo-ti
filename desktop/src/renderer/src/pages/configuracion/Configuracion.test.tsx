import { cleanup, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CompanySettings } from '@monitoreoti/shared';
import { fakePanelApi, fakeSettingsApi, renderWithPanel } from '../../test/fakes';
import { ConfiguracionPage } from './ConfiguracionPage';

afterEach(cleanup);

const company: CompanySettings = {
  name: 'Caribe Tours',
  taxId: null,
  contactEmail: null,
  phone: null,
  planName: 'Profesional',
  agents: { used: 2, max: 50 },
  branches: { used: 1, max: 1 },
  users: { used: 1, max: null },
  modules: ['monitoreo', 'remoto'],
};

describe('Configuración (W-40)', () => {
  it('guarda los datos de la empresa y muestra el uso del plan', async () => {
    const saveCompany = vi.fn(() => Promise.resolve({ id: 1 }));
    const onCompanyRenamed = vi.fn();
    renderWithPanel(
      <ConfiguracionPage onCompanyRenamed={onCompanyRenamed} />,
      fakePanelApi({ configuracion: fakeSettingsApi({ company: () => Promise.resolve(company), saveCompany }) }),
    );

    expect(await screen.findByRole('meter', { name: 'Sucursales: 1 de 1' })).toBeTruthy();
    expect(screen.getByText(/sin límite/)).toBeTruthy();
    expect(screen.getByText('Acceso remoto')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Nombre *'), { target: { value: 'Caribe Tours SRL' } });
    // El RNC solo acepta dígitos.
    fireEvent.change(screen.getByLabelText('RNC'), { target: { value: '1-01-12345-6' } });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar datos' }));

    await waitFor(() =>
      expect(saveCompany).toHaveBeenCalledWith({
        nombre: 'Caribe Tours SRL',
        rnc: '101123456',
        correoContacto: null,
        telefono: null,
      }),
    );
    await waitFor(() => expect(onCompanyRenamed).toHaveBeenCalled());
  });

  it('guarda el color de la empresa y vuelve a los originales; rechaza un logo grande', async () => {
    const setColor = vi.fn(() => Promise.resolve({ id: 1 }));
    const setLogo = vi.fn(() => Promise.resolve({ id: 1 }));
    renderWithPanel(
      <ConfiguracionPage initialTab="apariencia" />,
      fakePanelApi({
        configuracion: fakeSettingsApi({
          appearance: () => Promise.resolve({ color: '#1d4ed8', logo: null }),
          setColor,
          setLogo,
        }),
      }),
    );

    const code = await screen.findByLabelText('Código');
    expect((code as HTMLInputElement).value).toBe('#1d4ed8');
    fireEvent.change(code, { target: { value: '#E11D48' } });
    fireEvent.click(screen.getByRole('button', { name: 'Guardar color' }));
    await waitFor(() => expect(setColor).toHaveBeenCalledWith('#e11d48'));

    fireEvent.click(screen.getByRole('button', { name: 'Volver a los colores originales' }));
    await waitFor(() => expect(setColor).toHaveBeenLastCalledWith(null));

    const big = new File([new Uint8Array(300 * 1024)], 'logo.png', { type: 'image/png' });
    fireEvent.change(screen.getByLabelText('Elegir el logo'), { target: { files: [big] } });
    expect(await screen.findByText(/pesa más de 256 KB/)).toBeTruthy();
    expect(setLogo).not.toHaveBeenCalled();
  });

  it('un departamento con equipos no se puede eliminar', async () => {
    renderWithPanel(
      <ConfiguracionPage initialTab="departamentos" />,
      fakePanelApi({
        configuracion: fakeSettingsApi({
          departments: () =>
            Promise.resolve([
              { id: 1, name: 'Contabilidad', machineCount: 3 },
              { id: 2, name: 'Archivo', machineCount: 0 },
            ]),
        }),
      }),
    );
    const row = (await screen.findByText('Contabilidad')).closest('tr')!;
    expect((within(row).getByRole('button', { name: 'Eliminar' }) as HTMLButtonElement).disabled).toBe(true);
    const empty = screen.getByText('Archivo').closest('tr')!;
    expect((within(empty).getByRole('button', { name: 'Eliminar' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('los ajustes de seguridad se guardan al marcarlos', async () => {
    const saveSecurity = vi.fn((input: { pedirPermiso: boolean; actualizacionAutomatica: boolean }) =>
      Promise.resolve(input),
    );
    renderWithPanel(
      <ConfiguracionPage initialTab="seguridad" />,
      fakePanelApi({
        configuracion: fakeSettingsApi({
          security: () => Promise.resolve({ pedirPermiso: false, actualizacionAutomatica: true }),
          saveSecurity,
        }),
      }),
    );
    fireEvent.click(await screen.findByRole('checkbox', { name: /Pedir permiso al usuario/ }));
    await waitFor(() =>
      expect(saveSecurity).toHaveBeenCalledWith({ pedirPermiso: true, actualizacionAutomatica: true }),
    );
  });

  it('dice si los correos se envían y manda uno de prueba', async () => {
    const testEmail = vi.fn(() => Promise.resolve({ enviado: true, correo: 'admin@caribe.test' }));
    renderWithPanel(
      <ConfiguracionPage initialTab="correo" />,
      fakePanelApi({
        configuracion: fakeSettingsApi({
          email: () => Promise.resolve({ mode: 'smtp', from: 'alertas@caribe.test', host: 'smtp.caribe.test' }),
          testEmail,
        }),
      }),
    );
    expect(await screen.findByText('Se envían')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /correo de prueba/ }));
    expect(await screen.findByText(/enviado a admin@caribe.test/)).toBeTruthy();
  });
});

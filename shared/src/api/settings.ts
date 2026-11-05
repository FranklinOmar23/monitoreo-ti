/** Configuración de la empresa (W-40) y la apariencia del panel. */

/** Usado frente al máximo del plan; max null = sin límite. */
export interface PlanUsage {
  used: number;
  max: number | null;
}

export interface CompanySettings {
  name: string;
  taxId: string | null;
  contactEmail: string | null;
  phone: string | null;
  planName: string;
  agents: PlanUsage;
  branches: PlanUsage;
  users: PlanUsage;
  /** monitoreo, remoto, red, reportes_avanzados. */
  modules: string[];
}

export interface CompanyInput {
  nombre: string;
  rnc?: string | null;
  correoContacto?: string | null;
  telefono?: string | null;
}

/** color #rrggbb y logo como data URL; null = los de Centinela. */
export interface Appearance {
  color: string | null;
  logo: string | null;
}

/** Departamento de una sucursal (decisión 79). */
export interface DepartmentItem {
  id: number;
  name: string;
  machineCount: number;
  branchId: number;
  branchName: string;
  responsibleId: number | null;
  responsibleName: string | null;
}

export interface DepartmentInput {
  nombre: string;
  responsableId: number | null;
}

export interface SecuritySettings {
  pedirPermiso: boolean;
  actualizacionAutomatica: boolean;
}

/** smtp: se envían; archivo: desarrollo, se guardan como archivo; sin_configurar: no se envían. */
export interface EmailStatus {
  mode: 'smtp' | 'archivo' | 'sin_configurar';
  from: string | null;
  host: string | null;
}

export interface SettingsApi {
  appearance(): Promise<Appearance>;
  company(): Promise<CompanySettings>;
  saveCompany(input: CompanyInput): Promise<{ id: number }>;
  setColor(color: string | null): Promise<{ id: number }>;
  /** El logo en base64 (sin el prefijo data:). */
  setLogo(base64: string): Promise<{ id: number }>;
  removeLogo(): Promise<{ id: number }>;
  /** Todos los departamentos, con su sucursal (se administran en cada sucursal). */
  departments(): Promise<DepartmentItem[]>;
  security(): Promise<SecuritySettings>;
  saveSecurity(input: SecuritySettings): Promise<SecuritySettings>;
  email(): Promise<EmailStatus>;
  /** enviado false: el servidor lo guardó como archivo (no llega a un buzón). */
  testEmail(): Promise<{ enviado: boolean; correo: string }>;
}

export function createSettingsApi(
  get: <T>(path: string, query?: Record<string, string | number | boolean | null | undefined>) => Promise<T>,
  send: <T>(method: string, path: string, body?: unknown) => Promise<T>,
): SettingsApi {
  return {
    appearance: () => get('/apariencia'),
    company: () => get('/configuracion/empresa'),
    saveCompany: (input) => send('PUT', '/configuracion/empresa', input),
    setColor: (color) => send('PUT', '/configuracion/apariencia/color', { color }),
    setLogo: (datos) => send('PUT', '/configuracion/apariencia/logo', { datos }),
    removeLogo: () => send('DELETE', '/configuracion/apariencia/logo'),
    departments: () => get('/configuracion/departamentos'),
    security: () => get('/configuracion/seguridad'),
    saveSecurity: (input) => send('PUT', '/configuracion/seguridad', input),
    email: () => get('/configuracion/correo'),
    testEmail: () => send('POST', '/configuracion/correo/prueba'),
  };
}

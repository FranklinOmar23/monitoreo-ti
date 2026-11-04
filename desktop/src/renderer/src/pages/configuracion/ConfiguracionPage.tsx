import { useState, type CSSProperties, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { CompanySettings, DepartmentItem, PlanUsage, SecuritySettings } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { Icon } from '@components/Icon';
import { Modal } from '@components/Modal';
import { PageHeader } from '@components/PageHeader';
import { Tabs } from '@components/Tabs';
import { usePanelApi } from '../../data/PanelData';
import { brandTones, isHexColor } from '../../theme/brand';

const TABS = [
  { id: 'empresa', label: 'Empresa y plan' },
  { id: 'apariencia', label: 'Apariencia' },
  { id: 'departamentos', label: 'Departamentos' },
  { id: 'seguridad', label: 'Seguridad y remoto' },
  { id: 'correo', label: 'Correo y avisos' },
];

/** El color de Centinela: el que queda si la empresa no elige otro. */
export const DEFAULT_COLOR = '#0f766e';
export const MAX_LOGO_BYTES = 256 * 1024;

const MODULE_LABEL: Record<string, string> = {
  monitoreo: 'Monitoreo',
  remoto: 'Acceso remoto',
  red: 'Red y data center',
  reportes_avanzados: 'Reportes avanzados',
};

interface ConfiguracionPageProps {
  /** Para los enlaces a las reglas de alertas y a Mi perfil. */
  onNavigate?: (page: string) => void;
  /** El nombre de la empresa cambió: la barra lateral lo muestra al recargar la sesión. */
  onCompanyRenamed?: () => void;
  initialTab?: string;
}

/** W-40 Configuración de la empresa (permiso configuracion.editar). */
export function ConfiguracionPage({ onNavigate, onCompanyRenamed, initialTab = 'empresa' }: ConfiguracionPageProps) {
  const [tab, setTab] = useState(initialTab);
  return (
    <div className="page">
      <PageHeader
        title="Configuración"
        subtitle="Datos de la empresa, apariencia del panel, departamentos y ajustes generales"
      />
      <Tabs items={TABS} activeId={tab} onChange={setTab} label="Secciones de la configuración" />
      {tab === 'empresa' && <CompanyTab onRenamed={onCompanyRenamed} />}
      {tab === 'apariencia' && <AppearanceTab />}
      {tab === 'departamentos' && <DepartmentsTab />}
      {tab === 'seguridad' && <SecurityTab />}
      {tab === 'correo' && <EmailTab onNavigate={onNavigate} />}
    </div>
  );
}

// ---------------------------------------------------------------- empresa y plan

function CompanyTab({ onRenamed }: { onRenamed?: () => void }) {
  const api = usePanelApi();
  const company = useQuery({ queryKey: ['configuracion', 'empresa'], queryFn: () => api.configuracion.company() });
  if (company.isPending) return <Loading />;
  if (company.isError) return <ErrorState error={company.error} onRetry={() => void company.refetch()} />;
  return (
    <div className="settings-grid">
      <Card title="Datos de la empresa">
        <CompanyForm key={company.dataUpdatedAt} company={company.data} onRenamed={onRenamed} />
      </Card>
      <Card title={`Plan ${company.data.planName}`}>
        <div className="plan-usage">
          <UsageRow label="Equipos con agente" usage={company.data.agents} />
          <UsageRow label="Sucursales" usage={company.data.branches} />
          <UsageRow label="Usuarios activos" usage={company.data.users} />
        </div>
        <p className="menu-label">Módulos incluidos</p>
        <div className="chip-list">
          {company.data.modules.map((m) => (
            <span key={m} className="chip">
              {MODULE_LABEL[m] ?? m}
            </span>
          ))}
        </div>
        <p className="muted note">Para cambiar de plan, comuníquese con su proveedor.</p>
      </Card>
    </div>
  );
}

function UsageRow({ label, usage }: { label: string; usage: PlanUsage }) {
  const percent = usage.max ? Math.min(100, Math.round((usage.used / usage.max) * 100)) : 0;
  const level = percent >= 100 ? 'critico' : percent >= 80 ? 'advertencia' : 'sano';
  return (
    <div className="usage-row">
      <span>{label}</span>
      <strong className="mono">
        {usage.used} {usage.max === null ? '· sin límite' : `de ${usage.max}`}
      </strong>
      {usage.max !== null && (
        <span
          className="usage-bar"
          data-level={level}
          role="meter"
          aria-label={`${label}: ${usage.used} de ${usage.max}`}
          aria-valuenow={usage.used}
          aria-valuemin={0}
          aria-valuemax={usage.max}
        >
          <span style={{ width: `${percent}%` }} />
        </span>
      )}
    </div>
  );
}

function CompanyForm({ company, onRenamed }: { company: CompanySettings; onRenamed?: () => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [form, setForm] = useState({
    nombre: company.name,
    rnc: company.taxId ?? '',
    correoContacto: company.contactEmail ?? '',
    telefono: company.phone ?? '',
  });
  const save = useMutation({
    mutationFn: () =>
      api.configuracion.saveCompany({
        nombre: form.nombre.trim(),
        rnc: form.rnc.trim() || null,
        correoContacto: form.correoContacto.trim() || null,
        telefono: form.telefono.trim() || null,
      }),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['configuracion', 'empresa'] });
      if (form.nombre.trim() !== company.name) onRenamed?.();
    },
  });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };

  return (
    <form className="form-grid" onSubmit={submit}>
      {save.isError && (
        <p className="auth-error form-span" role="alert">
          {save.error.message}
        </p>
      )}
      {save.isSuccess && (
        <p className="auth-info form-span" role="status">
          Datos guardados.
        </p>
      )}
      <label className="field form-span">
        <span>Nombre *</span>
        <input
          required
          maxLength={150}
          value={form.nombre}
          onChange={(e) => setForm({ ...form, nombre: e.target.value })}
        />
      </label>
      <label className="field">
        <span>RNC</span>
        <input
          inputMode="numeric"
          maxLength={11}
          placeholder="9 dígitos, sin guiones"
          value={form.rnc}
          onChange={(e) => setForm({ ...form, rnc: e.target.value.replace(/\D/g, '') })}
        />
      </label>
      <label className="field">
        <span>Teléfono</span>
        <input maxLength={20} value={form.telefono} onChange={(e) => setForm({ ...form, telefono: e.target.value })} />
      </label>
      <label className="field form-span">
        <span>Correo de contacto</span>
        <input
          type="email"
          maxLength={200}
          value={form.correoContacto}
          onChange={(e) => setForm({ ...form, correoContacto: e.target.value })}
        />
      </label>
      <div className="form-span">
        <button type="submit" className="btn btn-primary" disabled={save.isPending}>
          {save.isPending ? 'Guardando…' : 'Guardar datos'}
        </button>
      </div>
    </form>
  );
}

// ---------------------------------------------------------------- apariencia

/** Lee el archivo elegido y devuelve su contenido en base64 (sin el prefijo data:). */
function readBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
    reader.onerror = () => reject(new Error('No se pudo leer el archivo.'));
    reader.readAsDataURL(file);
  });
}

function AppearanceTab() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const look = useQuery({ queryKey: ['apariencia'], queryFn: () => api.configuracion.appearance() });
  const refresh = () => void queries.invalidateQueries({ queryKey: ['apariencia'] });
  const [problem, setProblem] = useState<string | null>(null);

  const logo = useMutation({
    mutationFn: async (file: File) => {
      if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type))
        throw new Error('El logo debe ser una imagen PNG, JPEG o WebP.');
      if (file.size > MAX_LOGO_BYTES) throw new Error('El logo pesa más de 256 KB. Use uno más chico.');
      return api.configuracion.setLogo(await readBase64(file));
    },
    onSuccess: refresh,
  });
  const removeLogo = useMutation({ mutationFn: () => api.configuracion.removeLogo(), onSuccess: refresh });

  if (look.isPending) return <Loading />;
  if (look.isError) return <ErrorState error={look.error} onRetry={() => void look.refetch()} />;
  const error = problem ?? logo.error?.message ?? removeLogo.error?.message ?? null;

  return (
    <div className="settings-grid">
      <Card title="Logo">
        {error && (
          <p className="auth-error" role="alert">
            {error}
          </p>
        )}
        <div className="logo-preview">
          {look.data.logo ? (
            <img src={look.data.logo} alt="Logo de la empresa" />
          ) : (
            <span className="muted">Sin logo: se ve el de Centinela.</span>
          )}
        </div>
        <p className="muted note">
          PNG, JPEG o WebP de hasta 256 KB; mejor horizontal y con fondo transparente. Se ve en el menú lateral.
        </p>
        <div className="ficha-actions">
          <label className="btn btn-primary">
            <Icon name="upload" size={16} /> {look.data.logo ? 'Cambiar logo' : 'Subir logo'}
            <input
              type="file"
              accept="image/png,image/jpeg,image/webp"
              className="visually-hidden"
              aria-label="Elegir el logo"
              disabled={logo.isPending}
              onChange={(e) => {
                const file = e.target.files?.[0];
                e.target.value = '';
                setProblem(null);
                if (file) logo.mutate(file);
              }}
            />
          </label>
          {look.data.logo && (
            <button
              type="button"
              className="btn btn-outline"
              disabled={removeLogo.isPending}
              onClick={() => removeLogo.mutate()}
            >
              Quitar logo
            </button>
          )}
        </div>
      </Card>
      <Card title="Color de la empresa">
        <ColorForm key={look.data.color ?? 'centinela'} current={look.data.color} onSaved={refresh} />
      </Card>
    </div>
  );
}

function ColorForm({ current, onSaved }: { current: string | null; onSaved: () => void }) {
  const api = usePanelApi();
  const [color, setColor] = useState(current ?? DEFAULT_COLOR);
  const save = useMutation({
    mutationFn: (value: string | null) => api.configuracion.setColor(value),
    onSuccess: onSaved,
  });
  const valid = isHexColor(color);
  const dark = typeof window !== 'undefined' && window.matchMedia?.('(prefers-color-scheme: dark)').matches;
  const tones = valid ? brandTones(color)[dark ? 'dark' : 'light'] : null;
  const preview = tones
    ? ({
        '--color-primary': tones.primary,
        '--color-primary-hover': tones.hover,
        '--color-on-primary': tones.onPrimary,
      } as CSSProperties)
    : undefined;
  const adjusted = tones && tones.primary !== color.toLowerCase();

  return (
    <div className="color-form">
      {save.isError && (
        <p className="auth-error" role="alert">
          {save.error.message}
        </p>
      )}
      <div className="color-pick">
        <input
          type="color"
          aria-label="Elegir el color"
          value={valid ? color : DEFAULT_COLOR}
          onChange={(e) => setColor(e.target.value)}
        />
        <label className="field">
          <span>Código</span>
          <input className="mono" maxLength={7} value={color} onChange={(e) => setColor(e.target.value.trim())} />
        </label>
      </div>
      {!valid && <p className="auth-error">Escriba el color como #rrggbb (por ejemplo #1d4ed8).</p>}
      <div className="brand-preview" style={preview} aria-label="Vista previa del color">
        <button type="button" className="btn btn-primary" tabIndex={-1}>
          Botón principal
        </button>
        <button type="button" className="btn btn-outline" tabIndex={-1}>
          Secundario
        </button>
        <span className="brand-preview-link">Un enlace</span>
        <span className="brand-preview-tab">Pestaña activa</span>
      </div>
      {adjusted && (
        <p className="muted note">
          Para que se lea bien sobre el fondo, el panel usa un tono un poco más {dark ? 'claro' : 'oscuro'}:{' '}
          {tones.primary}.
        </p>
      )}
      <p className="muted note">Los colores de estado (sano, advertencia, crítico) no cambian.</p>
      <div className="ficha-actions">
        <button
          type="button"
          className="btn btn-primary"
          disabled={!valid || save.isPending || color.toLowerCase() === (current ?? '')}
          onClick={() => save.mutate(color.toLowerCase())}
        >
          Guardar color
        </button>
        {current && (
          <button type="button" className="btn btn-outline" disabled={save.isPending} onClick={() => save.mutate(null)}>
            Volver a los colores originales
          </button>
        )}
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- departamentos

function DepartmentsTab() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const list = useQuery({
    queryKey: ['configuracion', 'departamentos'],
    queryFn: () => api.configuracion.departments(),
  });
  const [editing, setEditing] = useState<DepartmentItem | 'new' | null>(null);
  const refresh = () => {
    void queries.invalidateQueries({ queryKey: ['configuracion', 'departamentos'] });
    // El formulario del equipo los lista.
    void queries.invalidateQueries({ queryKey: ['departamentos'] });
  };
  const remove = useMutation({
    mutationFn: (id: number) => api.configuracion.deleteDepartment(id),
    onSuccess: refresh,
  });

  if (list.isPending) return <Loading />;
  if (list.isError) return <ErrorState error={list.error} onRetry={() => void list.refetch()} />;

  return (
    <Card
      title="Departamentos"
      aside={
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>
          <Icon name="plus" size={16} /> Nuevo departamento
        </button>
      }
    >
      <p className="muted">
        Sirven para ordenar los equipos (Contabilidad, Caja, Recursos Humanos…) y filtrarlos en la lista.
      </p>
      {remove.isError && (
        <p className="auth-error" role="alert">
          {remove.error.message}
        </p>
      )}
      {list.data.length === 0 ? (
        <EmptyState title="Todavía no hay departamentos" />
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>Nombre</th>
              <th>Equipos</th>
              <th aria-label="Acciones" />
            </tr>
          </thead>
          <tbody>
            {list.data.map((d) => (
              <tr key={d.id}>
                <td>{d.name}</td>
                <td className="mono">{d.machineCount}</td>
                <td className="row-actions">
                  <button type="button" className="btn btn-outline" onClick={() => setEditing(d)}>
                    Renombrar
                  </button>
                  <button
                    type="button"
                    className="btn btn-outline"
                    disabled={remove.isPending || d.machineCount > 0}
                    title={
                      d.machineCount > 0 ? 'Tiene equipos: páselos a otro departamento antes de eliminarlo' : undefined
                    }
                    onClick={() => {
                      if (window.confirm(`¿Eliminar el departamento ${d.name}?`)) remove.mutate(d.id);
                    }}
                  >
                    Eliminar
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && (
        <DepartmentForm
          department={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            refresh();
            setEditing(null);
          }}
        />
      )}
    </Card>
  );
}

function DepartmentForm({
  department,
  onClose,
  onSaved,
}: {
  department: DepartmentItem | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const api = usePanelApi();
  const [name, setName] = useState(department?.name ?? '');
  const save = useMutation({
    mutationFn: () => api.configuracion.saveDepartment(department?.id ?? null, name.trim()),
    onSuccess: onSaved,
  });
  const submit = (event: FormEvent) => {
    event.preventDefault();
    save.mutate();
  };
  return (
    <Modal
      title={department ? `Renombrar ${department.name}` : 'Nuevo departamento'}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button
            type="submit"
            form="department-form"
            className="btn btn-primary"
            disabled={save.isPending || !name.trim()}
          >
            {save.isPending ? 'Guardando…' : 'Guardar'}
          </button>
        </>
      }
    >
      <form id="department-form" className="form-grid" onSubmit={submit}>
        {save.isError && (
          <p className="auth-error form-span" role="alert">
            {save.error.message}
          </p>
        )}
        <label className="field form-span">
          <span>Nombre *</span>
          <input required autoFocus maxLength={100} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
      </form>
    </Modal>
  );
}

// ---------------------------------------------------------------- seguridad y remoto

function SecurityTab() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const settings = useQuery({ queryKey: ['configuracion', 'seguridad'], queryFn: () => api.configuracion.security() });
  const save = useMutation({
    mutationFn: (input: SecuritySettings) => api.configuracion.saveSecurity(input),
    onSuccess: (data) => {
      queries.setQueryData(['configuracion', 'seguridad'], data);
      // Los mismos ajustes se ven en Acceso remoto e Instaladores.
      void queries.invalidateQueries({ queryKey: ['remoto'] });
      void queries.invalidateQueries({ queryKey: ['actualizaciones'] });
    },
  });

  if (settings.isPending) return <Loading />;
  if (settings.isError) return <ErrorState error={settings.error} onRetry={() => void settings.refetch()} />;
  const current = settings.data;

  return (
    <Card title="Seguridad y acceso remoto">
      {save.isError && (
        <p className="auth-error" role="alert">
          {save.error.message}
        </p>
      )}
      <label className="check setting-row">
        <input
          type="checkbox"
          checked={current.pedirPermiso}
          disabled={save.isPending}
          onChange={(e) => save.mutate({ ...current, pedirPermiso: e.target.checked })}
        />
        <span>
          <strong>Pedir permiso al usuario antes de ver su pantalla</strong>
          <span className="muted">
            Si hay alguien con la sesión iniciada, tiene 30 segundos para aceptar. Con el equipo bloqueado o sin nadie,
            el técnico entra sin preguntar.
          </span>
        </span>
      </label>
      <label className="check setting-row">
        <input
          type="checkbox"
          checked={current.actualizacionAutomatica}
          disabled={save.isPending}
          onChange={(e) => save.mutate({ ...current, actualizacionAutomatica: e.target.checked })}
        />
        <span>
          <strong>Actualizar los agentes automáticamente</strong>
          <span className="muted">
            Cuando se publica una versión nueva, los equipos se actualizan solos de a 10. Si lo apaga, se actualizan a
            mano desde Instaladores.
          </span>
        </span>
      </label>
      <p className="muted note">
        La verificación en dos pasos es obligatoria para los roles con acceso remoto; se ajusta en Usuarios → Roles y
        permisos.
      </p>
    </Card>
  );
}

// ---------------------------------------------------------------- correo y avisos

function EmailTab({ onNavigate }: { onNavigate?: (page: string) => void }) {
  const api = usePanelApi();
  const status = useQuery({ queryKey: ['configuracion', 'correo'], queryFn: () => api.configuracion.email() });
  const test = useMutation({ mutationFn: () => api.configuracion.testEmail() });

  if (status.isPending) return <Loading />;
  if (status.isError) return <ErrorState error={status.error} onRetry={() => void status.refetch()} />;
  const mail = status.data;

  return (
    <div className="settings-grid">
      <Card title="Correo">
        <p className="profile-status">
          <span className="status-badge" data-status={mail.mode === 'smtp' ? 'sano' : 'advertencia'}>
            {mail.mode === 'smtp' ? 'Se envían' : 'No se envían'}
          </span>
          <span className="muted">
            {mail.mode === 'smtp'
              ? `Por ${mail.host ?? 'el servidor de correo'}, desde ${mail.from ?? '—'}.`
              : mail.mode === 'archivo'
                ? 'Servidor de desarrollo: los correos se guardan como archivo en el servidor. Los enlaces de contraseña se entregan a mano.'
                : 'El servidor no tiene correo configurado (Correo:Smtp). Pídale al proveedor que lo configure.'}
          </span>
        </p>
        {test.isError && (
          <p className="auth-error" role="alert">
            {test.error.message}
          </p>
        )}
        {test.isSuccess && (
          <p className="auth-info" role="status">
            {test.data.enviado
              ? `Correo de prueba enviado a ${test.data.correo}. Revise su bandeja (y el correo no deseado).`
              : 'El servidor lo guardó como archivo: no llega a un buzón.'}
          </p>
        )}
        <button type="button" className="btn btn-outline" disabled={test.isPending} onClick={() => test.mutate()}>
          <Icon name="bell" size={16} /> Enviarme un correo de prueba
        </button>
      </Card>
      <Card title="Avisos">
        <p className="muted">
          El correo de una alerta crítica llega una sola vez a quienes gestionan alertas en su sucursal. Cada usuario
          puede apagarlo en Mi perfil.
        </p>
        <div className="ficha-actions">
          <button type="button" className="btn btn-outline" onClick={() => onNavigate?.('alertas')}>
            Reglas de alertas
          </button>
          <button type="button" className="btn btn-outline" onClick={() => onNavigate?.('perfil')}>
            Mi perfil
          </button>
        </div>
      </Card>
    </div>
  );
}

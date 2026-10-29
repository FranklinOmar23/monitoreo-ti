import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { CurrentSession, MachineDetail, TicketPriority } from '@monitoreoti/shared';
import type { NavItem } from '@components/Sidebar';
import { Sidebar } from '@components/Sidebar';
import { Topbar, type ProfileMenuItem } from '@components/Topbar';
import { PageHeader } from '@components/PageHeader';
import { LoginScreen } from './auth/LoginScreen';
import { SessionProvider, SplashScreen, useSession } from './auth/SessionProvider';
import { PanelDataProvider, usePanelApi } from './data/PanelData';
import { AlertasPage } from './pages/AlertasPage';
import { DashboardPage } from './pages/DashboardPage';
import { EquiposPage } from './pages/EquiposPage';
import { FichaPage } from './pages/FichaPage';
import { InstaladoresPage } from './pages/InstaladoresPage';
import { MachineForm, RetireDialog } from './pages/MachineForm';
import { NewTicketDialog } from './pages/NewTicketDialog';
import { TicketsPage } from './pages/TicketsPage';
import { QrPrintPage } from './pages/QrPrintPage';
import { RedPage } from './pages/red/RedPage';
import { SucursalesPage } from './pages/SucursalesPage';

const NAV_ITEMS: NavItem[] = [
  { id: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
  { id: 'equipos', label: 'Equipos', icon: 'monitor' },
  { id: 'sucursales', label: 'Sucursales', icon: 'building' },
  { id: 'alertas', label: 'Alertas', icon: 'bell' },
  { id: 'tickets', label: 'Tickets', icon: 'ticket' },
  { id: 'remoto', label: 'Acceso remoto', icon: 'remote' },
  { id: 'red', label: 'Red', icon: 'network' },
  { id: 'reportes', label: 'Reportes', icon: 'chart' },
  { id: 'instaladores', label: 'Instaladores', icon: 'download' },
  { id: 'usuarios', label: 'Usuarios', icon: 'users' },
  { id: 'configuracion', label: 'Configuración', icon: 'gear' },
];

export function App() {
  return (
    <SessionProvider>
      <SessionGate />
    </SessionProvider>
  );
}

/** Sin sesión: pantallas de acceso. Con sesión: el panel. */
function SessionGate() {
  const { state, api, completeLogin, logout, switchCompany } = useSession();

  if (state.status === 'loading') return <SplashScreen />;
  if (state.status === 'anonymous') return <LoginScreen api={api} onSession={completeLogin} notice={state.notice} />;

  const { session } = state;
  const menu: ProfileMenuItem[] = [
    ...session.empresas
      .filter((company) => company.id !== session.empresa?.id)
      .map((company) => ({ label: `Cambiar a ${company.nombre}`, onSelect: () => void switchCompany(company.id) })),
    { label: 'Cerrar sesión', onSelect: () => void logout() },
  ];

  return (
    <PanelDataProvider>
      <Shell session={session} menu={menu} />
    </PanelDataProvider>
  );
}

interface ShellProps {
  session: CurrentSession;
  menu: readonly ProfileMenuItem[];
}

/** Pantallas que ya existen; el resto del menú se construye en fases posteriores. */
const BUILT_PAGES = new Set(['dashboard', 'equipos', 'sucursales', 'alertas', 'tickets', 'red', 'instaladores', 'qr']);

/** Ticket por crear: desde la ficha (equipo) o desde una alerta (con su texto ya propuesto). */
interface NewTicketRequest {
  machineId?: number;
  machineName?: string;
  alertId?: number;
  title?: string;
  description?: string;
  priority?: TicketPriority;
}

/** Alertas abiertas para la insignia del menú y la campana (se refresca en vivo con SignalR). */
function useOpenAlerts(enabled: boolean) {
  const api = usePanelApi();
  const query = useQuery({
    queryKey: ['alertas', 'resumen'],
    queryFn: () => api.alerts({ tamano: 1 }),
    enabled,
    refetchInterval: 60_000,
  });
  return query.data?.counts;
}

/** Estructura del panel: barra lateral, barra superior y la pantalla activa. */
export function Shell({ session, menu }: ShellProps) {
  const can = (permission: string) => session.permisos.includes(permission);
  const [page, setPage] = useState(can('dashboard.ver') ? 'dashboard' : 'equipos');
  const [equipoId, setEquipoId] = useState<number | null>(null);
  const [query, setQuery] = useState('');
  const [branchFilter, setBranchFilter] = useState<number | null>(null);
  const [qrIds, setQrIds] = useState<number[]>([]);
  const [form, setForm] = useState<{ kind: 'nuevo' } | { kind: 'editar' | 'baja'; machine: MachineDetail } | null>(
    null,
  );

  const [ticketId, setTicketId] = useState<number | null>(null);
  const [alertId, setAlertId] = useState<number | null>(null);
  const [newTicket, setNewTicket] = useState<NewTicketRequest | null>(null);

  const alerts = useOpenAlerts(can('alertas.ver'));
  const canTickets = can('tickets.crear') || can('tickets.gestionar');

  // El menú muestra solo lo que el rol puede usar.
  const navItems = NAV_ITEMS.filter(
    (item) =>
      (item.id !== 'dashboard' || can('dashboard.ver')) &&
      (item.id !== 'instaladores' || can('instaladores.generar')) &&
      (item.id !== 'alertas' || can('alertas.ver')) &&
      (item.id !== 'tickets' || canTickets) &&
      (item.id !== 'red' || can('red.ver')),
  ).map((item) => (item.id === 'alertas' && alerts?.open ? { ...item, badge: alerts.open } : item));

  const navigate = (id: string) => {
    setPage(id);
    setEquipoId(null);
    setBranchFilter(null);
    setTicketId(null);
    setAlertId(null);
  };

  const openTicket = (id: number) => {
    navigate('tickets');
    setTicketId(id);
  };

  const openAlert = (id: number) => {
    navigate('alertas');
    setAlertId(id);
  };

  const openEquipo = (id: number) => {
    setPage('equipos');
    setEquipoId(id);
  };

  const changeQuery = (value: string) => {
    setQuery(value);
    if (page !== 'equipos' || equipoId !== null) navigate('equipos');
  };

  const companyName = session.empresa?.nombre ?? (session.usuario.esSuperadmin ? 'Proveedor del sistema' : '—');
  const roleName = session.empresa?.rol ?? (session.usuario.esSuperadmin ? 'Superadministrador' : '');

  return (
    <div className="app">
      <Sidebar
        items={navItems}
        activeId={page === 'qr' ? 'equipos' : page}
        onNavigate={navigate}
        empresa={{ nombre: companyName }}
      />
      <div className="main">
        <Topbar
          search={query}
          onSearchChange={changeQuery}
          user={{ nombre: session.usuario.nombre, rol: roleName }}
          hasUnreadAlerts={(alerts?.new ?? 0) > 0}
          menu={menu}
        />
        <main className="content">
          {page === 'dashboard' && <DashboardPage onOpenEquipo={openEquipo} onNavigate={navigate} />}
          {page === 'equipos' &&
            (equipoId !== null ? (
              <FichaPage
                key={equipoId}
                machineId={equipoId}
                onBack={() => setEquipoId(null)}
                onEdit={can('equipos.editar') ? (machine) => setForm({ kind: 'editar', machine }) : undefined}
                onRetire={can('equipos.editar') ? (machine) => setForm({ kind: 'baja', machine }) : undefined}
                onCreateTicket={
                  canTickets
                    ? (machine) => setNewTicket({ machineId: machine.id, machineName: machine.name })
                    : undefined
                }
              />
            ) : (
              <EquiposPage
                key={branchFilter ?? 'todas'}
                query={query}
                onQueryChange={changeQuery}
                onOpenEquipo={openEquipo}
                initialBranchId={branchFilter}
                onAddMachine={can('equipos.editar') ? () => setForm({ kind: 'nuevo' }) : undefined}
                onPrintQr={(ids) => {
                  setQrIds(ids);
                  setPage('qr');
                }}
              />
            ))}
          {page === 'qr' && <QrPrintPage machineIds={qrIds} onBack={() => setPage('equipos')} />}
          {page === 'sucursales' && (
            <SucursalesPage
              canEdit={can('sucursales.editar')}
              onOpenMachines={(branchId) => {
                setPage('equipos');
                setEquipoId(null);
                setBranchFilter(branchId);
              }}
            />
          )}
          {page === 'alertas' && can('alertas.ver') && (
            <AlertasPage
              key={alertId ?? 'lista'}
              canManage={can('alertas.gestionar')}
              onOpenEquipo={openEquipo}
              initialAlertId={alertId}
              tickets={
                canTickets
                  ? {
                      onOpen: openTicket,
                      onCreate: (alert) =>
                        setNewTicket({
                          alertId: alert.id,
                          machineName: alert.machineName ?? undefined,
                          title: alert.message,
                          description: [alert.detail, alert.advice && `Qué hacer: ${alert.advice}`]
                            .filter(Boolean)
                            .join('\n\n'),
                          priority: alert.level === 'critico' ? 'alta' : 'media',
                        }),
                    }
                  : undefined
              }
            />
          )}
          {page === 'tickets' && canTickets && (
            <TicketsPage
              key={ticketId ?? 'lista'}
              canManage={can('tickets.gestionar')}
              onOpenEquipo={openEquipo}
              onOpenAlert={openAlert}
              initialTicketId={ticketId}
            />
          )}
          {page === 'red' && can('red.ver') && <RedPage canEdit={can('red.editar')} onOpenEquipo={openEquipo} />}
          {page === 'instaladores' && can('instaladores.generar') && <InstaladoresPage />}
          {!BUILT_PAGES.has(page) && (
            <PageHeader
              title={NAV_ITEMS.find((item) => item.id === page)?.label ?? ''}
              subtitle="Esta pantalla se construye en una fase posterior."
            />
          )}
        </main>
        {newTicket && (
          <NewTicketDialog
            machineId={newTicket.machineId}
            machineName={newTicket.machineName}
            alertId={newTicket.alertId}
            defaultTitle={newTicket.title}
            defaultDescription={newTicket.description}
            defaultPriority={newTicket.priority}
            canAssign={can('tickets.gestionar')}
            onClose={() => setNewTicket(null)}
            onCreated={(id) => {
              setNewTicket(null);
              openTicket(id);
            }}
          />
        )}
        {form?.kind === 'nuevo' && (
          <MachineForm
            onClose={() => setForm(null)}
            onSaved={(id) => {
              setForm(null);
              openEquipo(id);
            }}
          />
        )}
        {form?.kind === 'editar' && (
          <MachineForm machine={form.machine} onClose={() => setForm(null)} onSaved={() => setForm(null)} />
        )}
        {form?.kind === 'baja' && (
          <RetireDialog
            machine={form.machine}
            onClose={() => setForm(null)}
            onRetired={() => {
              setForm(null);
              setEquipoId(null);
            }}
          />
        )}
      </div>
    </div>
  );
}

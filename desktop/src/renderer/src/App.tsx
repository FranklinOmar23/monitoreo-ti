import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { CurrentSession, MachineDetail, SessionTicket, SessionType, TicketPriority } from '@monitoreoti/shared';
import type { NavItem } from '@components/Sidebar';
import { Sidebar } from '@components/Sidebar';
import { Topbar, type NotificationItem, type ProfileMenuItem } from '@components/Topbar';
import { PageHeader } from '@components/PageHeader';
import { LoginScreen } from './auth/LoginScreen';
import { SessionProvider, SplashScreen, useSession } from './auth/SessionProvider';
import { PanelDataProvider, usePanelApi, useRealtime } from './data/PanelData';
import { AlertasPage } from './pages/AlertasPage';
import { CalendarioPage, MaintenanceForm, type ScheduleRequest } from './pages/CalendarioPage';
import { RemotoPage } from './pages/remoto/RemotoPage';
import { RemoteSessionDialog } from './pages/remoto/RemoteSession';
import { ConnectDialog, type DesktopChoice } from './pages/remoto/ConnectDialog';
import { FileExplorer } from './pages/remoto/FileExplorer';
import { RunScriptDialog, type MachineRef } from './pages/remoto/RunScriptDialog';
import { DashboardPage } from './pages/DashboardPage';
import { EquiposPage } from './pages/EquiposPage';
import { FichaPage } from './pages/FichaPage';
import { PerfilPage } from './pages/PerfilPage';
import { ConfiguracionPage } from './pages/configuracion/ConfiguracionPage';
import { ReportesPage } from './pages/reportes/ReportesPage';
import { useDeepLink } from './data/deepLinks';
import { TopLoader } from './TopLoader';
import { UpdateBanner } from './UpdateBanner';
import { applyBrandColor, cacheAppearance } from './theme/brand';
import { InstaladoresPage } from './pages/InstaladoresPage';
import { MachineForm, RetireDialog } from './pages/MachineForm';
import { NewTicketDialog } from './pages/NewTicketDialog';
import { TicketsPage } from './pages/TicketsPage';
import { QrPrintPage } from './pages/QrPrintPage';
import { RedPage } from './pages/red/RedPage';
import { View3DWindows } from './pages/red/View3DWindows';
import { SucursalesPage } from './pages/SucursalesPage';
import { UsuariosPage } from './pages/usuarios/UsuariosPage';

const NAV_ITEMS: NavItem[] = [
  { id: 'dashboard', label: 'Dashboard', icon: 'dashboard', section: 'Monitoreo' },
  { id: 'equipos', label: 'Equipos', icon: 'monitor', section: 'Monitoreo' },
  { id: 'sucursales', label: 'Sucursales', icon: 'building', section: 'Monitoreo' },
  { id: 'alertas', label: 'Alertas', icon: 'bell', section: 'Monitoreo' },
  { id: 'tickets', label: 'Tickets', icon: 'ticket', section: 'Monitoreo' },
  { id: 'calendario', label: 'Mantenimiento', icon: 'calendar', section: 'Monitoreo' },
  { id: 'remoto', label: 'Acceso remoto', icon: 'remote', section: 'Infraestructura' },
  { id: 'red', label: 'Red', icon: 'network', section: 'Infraestructura' },
  { id: 'reportes', label: 'Reportes', icon: 'chart', section: 'Infraestructura' },
  { id: 'instaladores', label: 'Instaladores', icon: 'download', section: 'Infraestructura' },
  { id: 'usuarios', label: 'Usuarios', icon: 'users', section: 'Administración' },
  { id: 'configuracion', label: 'Configuración', icon: 'gear', section: 'Administración' },
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
  const { state, api, completeLogin, logout, switchCompany, reloadSession } = useSession();
  // Sin sesión, los colores de Centinela (la empresa se conoce al entrar).
  useEffect(() => {
    if (state.status !== 'anonymous') return;
    applyBrandColor(null);
    void window.monitoreo?.tray?.setCriticalCount(0).catch(() => undefined);
  }, [state.status]);

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
      <View3DWindows>
        <Shell session={session} menu={menu} onProfileChanged={() => void reloadSession()} />
      </View3DWindows>
    </PanelDataProvider>
  );
}

interface ShellProps {
  session: CurrentSession;
  menu: readonly ProfileMenuItem[];
  /** Cambió el nombre en Mi perfil: se vuelve a leer la sesión para mostrarlo en la barra superior. */
  onProfileChanged?: () => void;
}

/** Pantallas que ya existen; el resto del menú se construye en fases posteriores. */
const BUILT_PAGES = new Set([
  'dashboard',
  'equipos',
  'sucursales',
  'alertas',
  'tickets',
  'calendario',
  'remoto',
  'red',
  'instaladores',
  'qr',
  'usuarios',
  'perfil',
  'configuracion',
  'reportes',
]);

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
    queryFn: () => api.alerts({ tamano: 8 }),
    enabled,
    refetchInterval: 60_000,
  });
  const items: NotificationItem[] | undefined = query.data?.page.items.map((a) => ({
    id: a.id,
    level: a.level,
    message: a.message,
    where: a.machineName ?? a.deviceName ?? null,
    at: a.createdAt,
  }));
  return query.data ? { ...query.data.counts, items: items! } : undefined;
}

/**
 * Aviso de Windows de cada alerta crítica nueva (llega en vivo por SignalR); al hacerle clic se abre la alerta.
 * Solo dentro del panel de escritorio (en las pruebas no hay puente con el proceso principal).
 */
function useCriticalDesktopNotifications(enabled: boolean, onOpen: (alertId: number) => void) {
  const api = usePanelApi();
  const realtime = useRealtime();
  // La función de abrir cambia en cada render; la suscripción no tiene por qué rehacerse por eso.
  const open = useRef(onOpen);
  useEffect(() => {
    open.current = onOpen;
  }, [onOpen]);
  useEffect(() => {
    if (!enabled || !realtime || !window.monitoreo) return;
    const bridge = window.monitoreo;
    const stopAlerts = realtime.onNewAlert((alertId) => {
      void api
        .alert(alertId)
        .then((alert) => {
          if (alert.level !== 'critico') return;
          const where = [alert.machineName ?? alert.deviceName, alert.branchName].filter(Boolean).join(' · ');
          return bridge.notify(`Crítico: ${alert.message}`.slice(0, 200), where.slice(0, 500), alertId);
        })
        .catch(() => undefined);
    });
    const stopClicks = bridge.onNotificationClick((alertId) => open.current(alertId));
    return () => {
      stopAlerts();
      stopClicks();
    };
  }, [enabled, realtime, api]);
}

/** Lo que necesita la ventana de una sesión además del ticket: opciones de W-28a y datos del equipo para W-29. */
interface SessionExtras {
  choice?: DesktopChoice;
  machine?: MachineDetail;
  technician?: string;
  /** El técnico tiene remoto.archivos: puede transferir archivos dentro del escritorio. */
  allowFiles?: boolean;
}

/**
 * En el panel de escritorio la terminal (W-30) y el escritorio remoto (W-28) abren en su propia ventana; sin el puente
 * (pruebas), no se entregan.
 */
function openSessionWindow(ticket: SessionTicket, extras: SessionExtras = {}): boolean {
  const bridge = window.monitoreo;
  if (!bridge || (ticket.type !== 'terminal' && ticket.type !== 'escritorio')) return false;
  void bridge.openTerminal({
    sessionId: ticket.sessionId,
    kind: ticket.type,
    relayPath: ticket.relayPath,
    token: ticket.token,
    machineName: ticket.machineName,
    machineId: ticket.machineId,
    quality: extras.choice?.calidad ?? 0,
    clipboardSync: extras.choice?.sincronizarPortapapeles ?? false,
    viewOnly: extras.choice?.soloVer ?? false,
    allowFiles: extras.allowFiles ?? false,
    technician: extras.technician,
    branchName: extras.machine?.branchName,
    agentVersion: extras.machine?.agentVersion ?? undefined,
    lastReportAt: extras.machine?.lastReportAt ?? undefined,
  });
  return true;
}

/** Estructura del panel: barra lateral, barra superior y la pantalla activa. */
export function Shell({ session, menu, onProfileChanged }: ShellProps) {
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
  const [runOn, setRunOn] = useState<MachineRef[] | null>(null);
  const [batchId, setBatchId] = useState<string | null>(null);

  // Logo y color de la empresa (W-40): se pintan al entrar y al cambiar de empresa, y quedan para la pantalla de carga.
  const panelApi = usePanelApi();
  const appearance = useQuery({
    queryKey: ['apariencia', session.empresa?.id ?? null],
    queryFn: () => panelApi.configuracion.appearance(),
  });
  useEffect(() => {
    if (!appearance.data) return;
    applyBrandColor(appearance.data.color);
    cacheAppearance(appearance.data);
  }, [appearance.data]);
  const canScripts = can('scripts.ejecutar') || can('scripts.editar');
  const remoteTypes = (['terminal', 'archivos', 'escritorio'] as const).filter((t) =>
    can(t === 'terminal' ? 'remoto.terminal' : t === 'archivos' ? 'remoto.archivos' : 'remoto.conectar'),
  );
  // Sesión remota en curso de pedirse: el escritorio pasa antes por "Antes de conectar" (W-28a).
  const [connectTo, setConnectTo] = useState<MachineDetail | null>(null);
  const [remoteSession, setRemoteSession] = useState<{
    machineId: number;
    machineName: string;
    type: SessionType;
    choice?: DesktopChoice;
    machine?: MachineDetail;
  } | null>(null);
  const closeRemoteSession = useCallback(() => setRemoteSession(null), []);
  // Lo último elegido por equipo: "Reintentar" y "Volver a conectar" en la ventana del escritorio lo repiten.
  const lastDesktop = useRef(new Map<number, { choice: DesktopChoice; machine: MachineDetail }>());
  const canFilesInDesktop = can('remoto.archivos');
  const technician = session.usuario.nombre;
  const handoff = useMemo(
    () =>
      remoteSession && remoteSession.type !== 'archivos'
        ? (ticket: SessionTicket) =>
            openSessionWindow(ticket, {
              choice: remoteSession.choice,
              machine: remoteSession.machine,
              technician,
              allowFiles: canFilesInDesktop,
            })
        : undefined,
    [remoteSession, technician, canFilesInDesktop],
  );
  const sessionOptions = useMemo(
    () =>
      remoteSession?.choice
        ? {
            motivo: remoteSession.choice.motivo || null,
            soloVer: remoteSession.choice.soloVer,
            pedirPermiso: remoteSession.choice.pedirPermiso,
          }
        : undefined,
    [remoteSession],
  );
  // Una ventana de sesión pide otra del mismo equipo (archivos, terminal o volver a conectar el escritorio).
  useEffect(() => {
    if (!window.monitoreo) return;
    return window.monitoreo.onSessionRequest(({ machineId, machineName, type }) => {
      if (!remoteTypes.includes(type)) return;
      const last = type === 'escritorio' ? lastDesktop.current.get(machineId) : undefined;
      setRemoteSession({ machineId, machineName, type, choice: last?.choice, machine: last?.machine });
    });
    // remoteTypes depende solo de los permisos de la sesión.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.permisos]);

  const alerts = useOpenAlerts(can('alertas.ver'));
  const canTickets = can('tickets.crear') || can('tickets.gestionar');
  const [schedule, setSchedule] = useState<ScheduleRequest | null>(null);

  // El menú muestra solo lo que el rol puede usar.
  const navItems = NAV_ITEMS.filter(
    (item) =>
      (item.id !== 'dashboard' || can('dashboard.ver')) &&
      (item.id !== 'instaladores' || can('instaladores.generar')) &&
      (item.id !== 'alertas' || can('alertas.ver')) &&
      (item.id !== 'tickets' || canTickets) &&
      (item.id !== 'calendario' || canTickets) &&
      (item.id !== 'remoto' || canScripts || remoteTypes.length > 0) &&
      (item.id !== 'red' || can('red.ver')) &&
      (item.id !== 'usuarios' || can('usuarios.administrar')) &&
      (item.id !== 'configuracion' || can('configuracion.editar')) &&
      (item.id !== 'reportes' || can('reportes.ver')),
  ).map((item) => (item.id === 'alertas' && alerts?.open ? { ...item, badge: alerts.open } : item));

  const navigate = (id: string) => {
    setPage(id);
    setEquipoId(null);
    setBranchFilter(null);
    setTicketId(null);
    setAlertId(null);
    setBatchId(null);
  };

  const openTicket = (id: number) => {
    navigate('tickets');
    setTicketId(id);
  };

  const openAlert = (id: number) => {
    navigate('alertas');
    setAlertId(id);
  };
  useCriticalDesktopNotifications(can('alertas.ver'), openAlert);

  const openEquipo = (id: number) => {
    setPage('equipos');
    setEquipoId(id);
  };

  // Clic en un equipo de una vista 3D en su ventana: se abre aquí, en Red.
  const [redDevice, setRedDevice] = useState<{ id: number; nonce: number } | null>(null);
  useEffect(() => {
    if (!window.monitoreo?.view3d) return;
    return window.monitoreo.view3d.onOpenDevice((id) => {
      if (!can('red.ver')) return;
      navigate('red');
      setRedDevice({ id, nonce: Date.now() });
    });
    // navigate y can solo dependen de los permisos de la sesión.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.permisos]);

  // Enlaces monitoreoti:// (sección 12.2): un equipo, o lo que diga un código QR.
  const [redTab, setRedTab] = useState<string | undefined>(undefined);
  useDeepLink((link) => {
    if (link.kind === 'maquina') {
      if (can('equipos.ver')) openEquipo(link.id);
      return true;
    }
    if (link.kind !== 'qr') return false;
    void panelApi
      .resolveQr(link.code)
      .then((target) => {
        if (target.type === 'maquina') openEquipo(target.id);
        else if (target.type === 'dispositivo') {
          setRedTab(undefined);
          navigate('red');
          setRedDevice({ id: target.id, nonce: Date.now() });
        } else {
          setRedTab(target.type === 'gabinete' ? 'datacenter' : 'rosetas');
          navigate('red');
          setRedDevice({ id: 0, nonce: Date.now() });
        }
      })
      .catch(() => window.alert('Ese código QR no corresponde a nada que usted pueda ver.'));
    return true;
  });

  // Bandeja: el punto rojo y el texto del menú siguen a las alertas críticas abiertas; su menú lleva a Alertas o al buscador.
  const criticalOpen = alerts?.critical ?? 0;
  useEffect(() => {
    void window.monitoreo?.tray?.setCriticalCount(criticalOpen).catch(() => undefined);
  }, [criticalOpen]);
  useEffect(() => {
    if (!window.monitoreo?.tray) return;
    return window.monitoreo.tray.onNavigate((target) => {
      if (target === 'alertas') {
        if (can('alertas.ver')) navigate('alertas');
        return;
      }
      document.querySelector<HTMLInputElement>('.search-global input')?.focus();
    });
    // navigate y can solo dependen de los permisos de la sesión.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session.permisos]);

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
        logo={appearance.data?.logo}
      />
      <div className="main">
        <Topbar
          search={query}
          onSearchChange={changeQuery}
          user={{ nombre: session.usuario.nombre, rol: roleName }}
          hasUnreadAlerts={(alerts?.new ?? 0) > 0}
          menu={[{ label: 'Mi perfil', onSelect: () => navigate('perfil') }, ...menu]}
          notifications={alerts?.items}
          onOpenNotification={openAlert}
          onSeeAllNotifications={() => navigate('alertas')}
        />
        <TopLoader />
        <UpdateBanner />
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
                onScheduleMaintenance={
                  can('tickets.gestionar')
                    ? (machine) => setSchedule({ machineId: machine.id, machineName: machine.name })
                    : undefined
                }
                onRunScript={
                  can('scripts.ejecutar') ? (machine) => setRunOn([{ id: machine.id, name: machine.name }]) : undefined
                }
                remoteTypes={remoteTypes}
                onOpenSession={(machine, type) =>
                  type === 'escritorio'
                    ? setConnectTo(machine)
                    : setRemoteSession({ machineId: machine.id, machineName: machine.name, type })
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
                onRunScript={can('scripts.ejecutar') ? setRunOn : undefined}
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
              canRunCommands={can('scripts.ejecutar')}
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
          {page === 'red' && can('red.ver') && (
            <RedPage
              key={redDevice?.nonce}
              canEdit={can('red.editar')}
              onOpenEquipo={openEquipo}
              onOpenAlerta={openAlert}
              initialTab={redTab}
              initialDeviceId={redDevice?.id || undefined}
            />
          )}
          {page === 'calendario' && canTickets && (
            <CalendarioPage canEdit={can('tickets.gestionar')} onOpenEquipo={openEquipo} />
          )}
          {page === 'remoto' && (canScripts || remoteTypes.length > 0) && (
            <RemotoPage
              key={batchId ?? 'remoto'}
              canRun={can('scripts.ejecutar')}
              canEdit={can('scripts.editar')}
              canSessions={remoteTypes.length > 0}
              canConfigure={can('usuarios.administrar')}
              initialBatchId={batchId}
              onOpenEquipo={openEquipo}
            />
          )}
          {page === 'instaladores' && can('instaladores.generar') && <InstaladoresPage />}
          {page === 'usuarios' && can('usuarios.administrar') && <UsuariosPage />}
          {page === 'reportes' && can('reportes.ver') && (
            <ReportesPage onOpenEquipo={openEquipo} companyName={companyName} logo={appearance.data?.logo} />
          )}
          {page === 'configuracion' && can('configuracion.editar') && (
            <ConfiguracionPage onNavigate={navigate} onCompanyRenamed={onProfileChanged} />
          )}
          {page === 'perfil' && (
            <PerfilPage companyName={companyName} roleName={roleName} onRenamed={onProfileChanged} />
          )}
          {!BUILT_PAGES.has(page) && (
            <PageHeader
              title={NAV_ITEMS.find((item) => item.id === page)?.label ?? ''}
              subtitle="Esta pantalla se construye en una fase posterior."
            />
          )}
        </main>
        {schedule && <MaintenanceForm request={schedule} onClose={() => setSchedule(null)} />}
        {connectTo && (
          <ConnectDialog
            machine={connectTo}
            onCancel={() => setConnectTo(null)}
            onConnect={(choice) => {
              lastDesktop.current.set(connectTo.id, { choice, machine: connectTo });
              setRemoteSession({
                machineId: connectTo.id,
                machineName: connectTo.name,
                type: 'escritorio',
                choice,
                machine: connectTo,
              });
              setConnectTo(null);
            }}
          />
        )}
        {remoteSession && (
          <RemoteSessionDialog
            key={`${remoteSession.machineId}-${remoteSession.type}`}
            machineId={remoteSession.machineId}
            machineName={remoteSession.machineName}
            type={remoteSession.type}
            onClose={closeRemoteSession}
            handoff={handoff}
            options={sessionOptions}
          >
            {remoteSession.type === 'archivos' ? (socket) => <FileExplorer socket={socket} /> : undefined}
          </RemoteSessionDialog>
        )}
        {runOn && (
          <RunScriptDialog
            machines={runOn}
            onClose={() => setRunOn(null)}
            onStarted={(id) => {
              setRunOn(null);
              navigate('remoto');
              setBatchId(id);
            }}
          />
        )}
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

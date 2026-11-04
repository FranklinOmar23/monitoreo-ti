import { hostname } from 'node:os';
import { join } from 'node:path';
import { existsSync, writeFileSync } from 'node:fs';
import { writeFile } from 'node:fs/promises';
import {
  BrowserWindow,
  Menu,
  Tray,
  app,
  clipboard,
  dialog,
  ipcMain,
  nativeImage,
  Notification,
  safeStorage,
  session,
  type IpcMainInvokeEvent,
  type NativeImage,
} from 'electron';
import { findDeepLink, PROTOCOL, type DeepLink } from './deep-link';
import { buildContentSecurityPolicy, deviceUserAgent, isAllowedNavigation } from './security';
import { resolveServerUrl } from './server-config';
import { isSafeFileName, isValidRefreshToken, MAX_SAVE_BYTES, SessionStore } from './session-store';
import { parseTerminalTicket, type TerminalTicket } from './terminal-ticket';
import { Transfers } from './transfers';
import { criticalLabel, trayIconBitmap } from './tray-icon';
import { PanelUpdates } from './updates';
import { parseView3DSnapshot, type View3DSnapshot } from './view3d';

const SERVER_URL = resolveServerUrl(process.env, app.isPackaged ? process.resourcesPath : null);
/** Al iniciar con Windows el panel arranca en la bandeja, sin abrir la ventana. */
const START_HIDDEN = process.argv.includes('--oculto');
const DEV_SERVER_URL = app.isPackaged ? undefined : process.env['ELECTRON_RENDERER_URL'];
const RENDERER_FILE = join(__dirname, '../renderer/index.html');

function appUrl(): string {
  return DEV_SERVER_URL ?? `file://${RENDERER_FILE.replace(/\\/g, '/')}`;
}

function applySessionSecurity(): void {
  const csp = buildContentSecurityPolicy({ serverUrl: SERVER_URL, devServerUrl: DEV_SERVER_URL });

  session.defaultSession.webRequest.onHeadersReceived((details, callback) => {
    callback({
      responseHeaders: { ...details.responseHeaders, 'Content-Security-Policy': [csp] },
    });
  });

  session.defaultSession.setUserAgent(deviceUserAgent(session.defaultSession.getUserAgent(), hostname()));

  // Cámara, micrófono, notificaciones web, etc.: nada se concede desde el renderer.
  session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
}

function createMainWindow(): BrowserWindow {
  const window = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1280,
    minHeight: 720,
    show: false,
    title: 'MonitoreoTI',
    backgroundColor: '#faf8f3',
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: '#00000000', symbolColor: '#6a7479', height: 64 },
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  });

  window.once('ready-to-show', () => {
    if (!START_HIDDEN || pendingLink) window.show();
  });
  // Cerrar la ventana la manda a la bandeja: el panel sigue avisando de las alertas críticas. Se sale con "Salir".
  window.on('close', (event) => {
    if (quitting) return;
    event.preventDefault();
    window.hide();
    showTrayHintOnce();
  });

  // Bloquear navegación y ventanas emergentes hacia fuera del panel.
  window.webContents.on('will-navigate', (event, url) => {
    if (!isAllowedNavigation(url, appUrl())) event.preventDefault();
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  if (DEV_SERVER_URL) {
    void window.loadURL(DEV_SERVER_URL);
  } else {
    void window.loadFile(RENDERER_FILE);
  }

  return window;
}

/** Tickets de terminal por ventana: cada ventana recoge el suyo una sola vez. */
const terminalTickets = new Map<number, TerminalTicket>();
/** Equipo de cada ventana de sesión abierta (para pedir sus archivos a la ventana principal). */
const sessionMachines = new Map<number, { machineId: number; machineName: string }>();
let mainWindow: BrowserWindow | null = null;
/** Salir de verdad ("Salir" de la bandeja, instalar una actualización o apagar Windows). */
let quitting = false;
let tray: Tray | null = null;
let criticalAlerts = 0;
/** Enlace que llegó antes de que la ventana pudiera recibirlo (lo recoge con deeplink:take). */
let pendingLink: DeepLink | null = null;
let updates: PanelUpdates | null = null;

function showMainWindow(): void {
  if (!mainWindow || mainWindow.isDestroyed()) mainWindow = createMainWindow();
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
}

/** Abre la pantalla del enlace en la ventana principal (si no hay sesión, el panel la guarda hasta entrar). */
function openDeepLink(link: DeepLink): void {
  pendingLink = link;
  showMainWindow();
  if (mainWindow && !mainWindow.webContents.isLoading()) mainWindow.webContents.send('deeplink:open');
}

/** La primera vez que se cierra la ventana se avisa que el panel sigue en la bandeja. */
function showTrayHintOnce(): void {
  const flag = join(app.getPath('userData'), 'bandeja-avisada');
  if (existsSync(flag)) return;
  try {
    writeFileSync(flag, '');
  } catch {
    // Sin poder guardar la marca el aviso puede repetirse: no es grave.
  }
  if (!Notification.isSupported()) return;
  const notification = new Notification({
    title: 'Centinela sigue abierto',
    body: 'Sigue avisando de las alertas críticas desde la bandeja. Para cerrarlo del todo use "Salir" en su ícono.',
  });
  notification.on('click', showMainWindow);
  notification.show();
}

function trayImage(badge: boolean): NativeImage {
  const image = nativeImage.createEmpty();
  for (const [size, scale] of [
    [16, 1],
    [24, 1.5],
    [32, 2],
  ] as const)
    image.addRepresentation({ scaleFactor: scale, width: size, height: size, buffer: trayIconBitmap(size, badge) });
  return image;
}

/** Ícono junto al reloj con el menú del panel (sección 12.2). */
function updateTray(): void {
  if (!tray) {
    tray = new Tray(trayImage(false));
    tray.on('click', showMainWindow);
  }
  tray.setImage(trayImage(criticalAlerts > 0));
  tray.setToolTip(`Centinela · ${criticalLabel(criticalAlerts)}`);
  const navigate = (target: 'alertas' | 'buscar') => () => {
    showMainWindow();
    mainWindow?.webContents.send('tray:navigate', target);
  };
  tray.setContextMenu(
    Menu.buildFromTemplate([
      { label: 'Abrir Centinela', click: showMainWindow },
      { label: criticalLabel(criticalAlerts), enabled: criticalAlerts > 0, click: navigate('alertas') },
      { label: 'Buscar equipo…', click: navigate('buscar') },
      { type: 'separator' },
      {
        label: 'Iniciar con Windows',
        type: 'checkbox',
        checked: app.getLoginItemSettings().openAtLogin,
        // En desarrollo apuntaría al electron.exe de node_modules.
        enabled: app.isPackaged,
        click: (item) => {
          app.setLoginItemSettings({ openAtLogin: item.checked, args: ['--oculto'] });
          updateTray();
        },
      },
      { type: 'separator' },
      {
        label: 'Salir',
        click: () => {
          quitting = true;
          app.quit();
        },
      },
    ]),
  );
}

/**
 * La terminal (W-30) y el escritorio remoto (W-28) en su propia ventana (sección 12), con el mismo endurecimiento que
 * la principal.
 */
function createTerminalWindow(ticket: TerminalTicket): void {
  const desktop = ticket.kind === 'escritorio';
  const window = new BrowserWindow({
    width: desktop ? 1366 : 1000,
    height: desktop ? 860 : 640,
    minWidth: 640,
    minHeight: 360,
    title: `${desktop ? 'Escritorio' : 'Terminal'} · ${ticket.machineName}`,
    backgroundColor: desktop ? '#05121a' : '#0b1f26',
    autoHideMenuBar: true,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  });
  const contentsId = window.webContents.id;
  terminalTickets.set(contentsId, ticket);
  if (ticket.machineId)
    sessionMachines.set(contentsId, { machineId: ticket.machineId, machineName: ticket.machineName });
  window.on('closed', () => {
    terminalTickets.delete(contentsId);
    sessionMachines.delete(contentsId);
  });
  window.webContents.on('will-navigate', (event, url) => {
    if (!isAllowedNavigation(url, appUrl())) event.preventDefault();
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  const hash = `${ticket.kind}/${ticket.sessionId}`;
  if (DEV_SERVER_URL) void window.loadURL(`${DEV_SERVER_URL}#${hash}`);
  else void window.loadFile(RENDERER_FILE, { hash });
}

/** Vistas 3D abiertas en su ventana (7.6): una por sala o gabinete, con los últimos datos que mandó el panel. */
const view3dWindows = new Map<string, BrowserWindow>();
const view3dData = new Map<number, View3DSnapshot>();

function showView3DWindow(snapshot: View3DSnapshot, open: boolean): void {
  const existing = view3dWindows.get(snapshot.key);
  if (existing && !existing.isDestroyed()) {
    view3dData.set(existing.webContents.id, snapshot);
    existing.setTitle(`Vista 3D · ${snapshot.title}`);
    existing.webContents.send('view3d:data', snapshot);
    if (open) {
      if (existing.isMinimized()) existing.restore();
      existing.focus();
    }
    return;
  }
  // Una actualización que llega cuando ya la cerraron no la vuelve a abrir.
  if (!open) return;

  const window = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 640,
    minHeight: 420,
    title: `Vista 3D · ${snapshot.title}`,
    backgroundColor: '#f3f0e8',
    autoHideMenuBar: true,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: true,
    },
  });
  const contentsId = window.webContents.id;
  view3dWindows.set(snapshot.key, window);
  view3dData.set(contentsId, snapshot);
  // La página no cambia el título (si no, Electron pondría el del documento).
  window.on('page-title-updated', (event) => event.preventDefault());
  window.on('closed', () => {
    view3dWindows.delete(snapshot.key);
    view3dData.delete(contentsId);
    if (mainWindow && !mainWindow.isDestroyed()) mainWindow.webContents.send('view3d:closed', snapshot.key);
  });
  window.webContents.on('will-navigate', (event, url) => {
    if (!isAllowedNavigation(url, appUrl())) event.preventDefault();
  });
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  const hash = `vista3d/${snapshot.key}`;
  if (DEV_SERVER_URL) void window.loadURL(`${DEV_SERVER_URL}#${hash}`);
  else void window.loadFile(RENDERER_FILE, { hash });
}

function registerIpc(): void {
  const sessions = new SessionStore(join(app.getPath('userData'), 'session.bin'), {
    isAvailable: () => safeStorage.isEncryptionAvailable(),
    encrypt: (plain) => safeStorage.encryptString(plain),
    decrypt: (data) => safeStorage.decryptString(data),
  });

  // Solo responde a la ventana del propio panel.
  const fromPanel = (event: IpcMainInvokeEvent) => {
    if (!event.senderFrame || !isAllowedNavigation(event.senderFrame.url, appUrl())) {
      throw new Error('Origen no permitido');
    }
  };

  ipcMain.handle('app:get-info', (event) => {
    fromPanel(event);
    return { name: 'MonitoreoTI', version: app.getVersion(), serverUrl: SERVER_URL };
  });
  ipcMain.handle('session:load', (event) => {
    fromPanel(event);
    return sessions.load();
  });
  ipcMain.handle('session:save', (event, token: unknown) => {
    fromPanel(event);
    if (!isValidRefreshToken(token)) throw new Error('Token de sesión inválido.');
    return sessions.save(token);
  });
  // Guardar un archivo que bajó el panel (el instalador del agente). Siempre pregunta dónde con el diálogo del sistema.
  ipcMain.handle('file:save', async (event, name: unknown, data: unknown) => {
    fromPanel(event);
    if (!isSafeFileName(name)) throw new Error('Nombre de archivo inválido.');
    if (!(data instanceof Uint8Array) || data.byteLength === 0 || data.byteLength > MAX_SAVE_BYTES)
      throw new Error('Archivo inválido.');

    const window = BrowserWindow.fromWebContents(event.sender);
    const options = { defaultPath: join(app.getPath('downloads'), name) };
    const result = window ? await dialog.showSaveDialog(window, options) : await dialog.showSaveDialog(options);
    if (result.canceled || !result.filePath) return { saved: false };
    try {
      await writeFile(result.filePath, data);
      return { saved: true, path: result.filePath };
    } catch (error) {
      // Archivo abierto (el instalador anterior sigue corriendo o el antivirus lo revisa) o carpeta sin permiso.
      const code = (error as NodeJS.ErrnoException).code;
      return {
        saved: false,
        error:
          code === 'EBUSY' || code === 'EPERM' || code === 'EACCES'
            ? `No se pudo guardar en ${result.filePath}: el archivo está en uso o no hay permiso. Ciérrelo (o espere a que termine el antivirus) o guárdelo con otro nombre.`
            : `No se pudo guardar el archivo (${code ?? 'error desconocido'}).`,
      };
    }
  });

  // Copiar al portapapeles. El renderer no tiene permiso de portapapeles (se niegan todos los permisos web).
  // Leer el portapapeles local: solo para "Pegar en el equipo" del escritorio remoto, a pedido del técnico.
  ipcMain.handle('clipboard:read', async (event) => {
    fromPanel(event);
    return (await clipboard.readText()).slice(0, 100_000);
  });
  ipcMain.handle('clipboard:write', (event, text: unknown) => {
    fromPanel(event);
    if (typeof text !== 'string' || text.length === 0 || text.length > 4_000) throw new Error('Texto inválido.');
    clipboard.writeText(text);
  });
  // Aviso nativo de una alerta crítica: el texto llega del renderer y se valida; el clic trae la ventana y abre la alerta.
  ipcMain.handle('notification:show', (event, title: unknown, body: unknown, alertId: unknown) => {
    fromPanel(event);
    if (typeof title !== 'string' || title.length === 0 || title.length > 200) throw new Error('Título inválido.');
    if (typeof body !== 'string' || body.length > 500) throw new Error('Texto inválido.');
    if (typeof alertId !== 'number' || !Number.isInteger(alertId) || alertId <= 0) throw new Error('Alerta inválida.');
    if (!Notification.isSupported()) return;
    const window = BrowserWindow.fromWebContents(event.sender);
    const notification = new Notification({ title, body });
    notification.on('click', () => {
      if (!window || window.isDestroyed()) return;
      if (window.isMinimized()) window.restore();
      window.show();
      window.focus();
      window.webContents.send('notification:click', alertId);
    });
    notification.show();
  });
  ipcMain.handle('terminal:open', (event, value: unknown) => {
    fromPanel(event);
    const ticket = parseTerminalTicket(value);
    if (!ticket) throw new Error('Ticket de terminal inválido.');
    createTerminalWindow(ticket);
  });
  // Solo la ventana creada para ese ticket lo recibe, y una sola vez.
  ipcMain.handle('terminal:ticket', (event) => {
    fromPanel(event);
    const ticket = terminalTickets.get(event.sender.id);
    terminalTickets.delete(event.sender.id);
    return ticket ? { ...ticket, serverUrl: SERVER_URL } : null;
  });
  // Archivos de las sesiones remotas (W-31): el disco local solo se toca aquí, con el diálogo del sistema.
  const transfers = new Transfers();
  const owners = new Set<number>();
  const ownerOf = (event: IpcMainInvokeEvent) => {
    const id = event.sender.id;
    if (!owners.has(id)) {
      owners.add(id);
      event.sender.once('destroyed', () => {
        owners.delete(id);
        void transfers.releaseOwner(id);
      });
    }
    return id;
  };
  const isId = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value > 0;

  ipcMain.handle('download:begin', async (event, name: unknown) => {
    fromPanel(event);
    if (!isSafeFileName(name)) throw new Error('Nombre de archivo inválido.');
    const window = BrowserWindow.fromWebContents(event.sender);
    const options = { defaultPath: join(app.getPath('downloads'), name) };
    const result = window ? await dialog.showSaveDialog(window, options) : await dialog.showSaveDialog(options);
    if (result.canceled || !result.filePath) return null;
    return { id: await transfers.beginDownload(ownerOf(event), result.filePath) };
  });
  ipcMain.handle('download:write', (event, id: unknown, data: unknown) => {
    fromPanel(event);
    if (!isId(id) || !(data instanceof Uint8Array)) throw new Error('Parte inválida.');
    return transfers.write(ownerOf(event), id, data);
  });
  ipcMain.handle('download:end', async (event, id: unknown, ok: unknown) => {
    fromPanel(event);
    if (!isId(id)) throw new Error('Descarga inválida.');
    return { path: await transfers.endDownload(ownerOf(event), id, ok === true) };
  });
  ipcMain.handle('upload:pick', async (event) => {
    fromPanel(event);
    const window = BrowserWindow.fromWebContents(event.sender);
    const options = { properties: ['openFile' as const] };
    const result = window ? await dialog.showOpenDialog(window, options) : await dialog.showOpenDialog(options);
    const path = result.filePaths[0];
    if (result.canceled || !path) return null;
    return transfers.beginUpload(ownerOf(event), path);
  });
  ipcMain.handle('upload:read', (event, id: unknown, offset: unknown, length: unknown) => {
    fromPanel(event);
    if (!isId(id) || typeof offset !== 'number' || typeof length !== 'number') throw new Error('Parte inválida.');
    return transfers.read(ownerOf(event), id, offset, length);
  });
  ipcMain.handle('upload:end', (event, id: unknown) => {
    fromPanel(event);
    if (!isId(id)) throw new Error('Subida inválida.');
    return transfers.endUpload(ownerOf(event), id);
  });
  // Desde una ventana de sesión: pedir a la ventana principal (que tiene la sesión del técnico) otra sesión del mismo
  // equipo: archivos, terminal o volver a conectar el escritorio.
  ipcMain.handle('remote:open-session', (event, type: unknown) => {
    fromPanel(event);
    const machine = sessionMachines.get(event.sender.id);
    if (type !== 'archivos' && type !== 'terminal' && type !== 'escritorio')
      throw new Error('Tipo de sesión inválido.');
    if (!machine || !mainWindow || mainWindow.isDestroyed()) return false;
    if (type === 'archivos') {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.show();
      mainWindow.focus();
    }
    mainWindow.webContents.send('remote:open-session', { ...machine, type });
    return true;
  });
  // Vistas 3D en ventana aparte (7.6): solo la ventana principal las abre y alimenta.
  ipcMain.handle('view3d:show', (event, value: unknown, open: unknown) => {
    fromPanel(event);
    if (!mainWindow || event.sender.id !== mainWindow.webContents.id) throw new Error('Origen no permitido');
    const snapshot = parseView3DSnapshot(value);
    if (!snapshot) throw new Error('Vista 3D inválida.');
    showView3DWindow(snapshot, open === true);
  });
  ipcMain.handle('view3d:data', (event) => {
    fromPanel(event);
    return view3dData.get(event.sender.id) ?? null;
  });
  ipcMain.handle('view3d:open-device', (event, deviceId: unknown) => {
    fromPanel(event);
    if (!view3dData.has(event.sender.id) || !isId(deviceId)) throw new Error('Equipo inválido.');
    if (!mainWindow || mainWindow.isDestroyed()) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
    mainWindow.webContents.send('view3d:open-device', deviceId);
  });
  ipcMain.handle('session:clear', (event) => {
    fromPanel(event);
    return sessions.clear();
  });
  // Bandeja: la ventana principal informa cuántas alertas críticas hay abiertas.
  ipcMain.handle('tray:alerts', (event, count: unknown) => {
    fromPanel(event);
    if (!mainWindow || event.sender.id !== mainWindow.webContents.id) throw new Error('Origen no permitido');
    if (typeof count !== 'number' || !Number.isInteger(count) || count < 0 || count > 1_000_000)
      throw new Error('Cantidad inválida.');
    if (count === criticalAlerts) return;
    criticalAlerts = count;
    updateTray();
  });
  // Enlace monitoreoti:// pendiente: se entrega una sola vez y solo a la ventana principal.
  ipcMain.handle('deeplink:take', (event) => {
    fromPanel(event);
    if (!mainWindow || event.sender.id !== mainWindow.webContents.id) return null;
    const link = pendingLink;
    pendingLink = null;
    return link;
  });
  ipcMain.handle('update:status', (event) => {
    fromPanel(event);
    return updates?.ready() ?? null;
  });
  ipcMain.handle('update:install', (event) => {
    fromPanel(event);
    if (!updates?.ready()) return;
    quitting = true;
    updates.install();
  });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  // Abrir el panel otra vez (o un enlace monitoreoti:// con el panel ya abierto) trae la ventana que ya existe.
  app.on('second-instance', (_event, argv) => {
    const link = findDeepLink(argv);
    if (link) openDeepLink(link);
    else showMainWindow();
  });
  app.on('before-quit', () => {
    quitting = true;
  });

  // En la app instalada (el instalador también lo registra); en desarrollo no se toca el registro de Windows.
  if (app.isPackaged) app.setAsDefaultProtocolClient(PROTOCOL);
  pendingLink = findDeepLink(process.argv);

  void app.whenReady().then(() => {
    applySessionSecurity();
    registerIpc();
    mainWindow = createMainWindow();
    updateTray();
    if (app.isPackaged) {
      updates = new PanelUpdates(SERVER_URL, () => mainWindow);
      updates.start();
    }

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) mainWindow = createMainWindow();
    });
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
  });
}

import { join } from 'node:path';
import { writeFile } from 'node:fs/promises';
import {
  BrowserWindow,
  app,
  clipboard,
  dialog,
  ipcMain,
  safeStorage,
  session,
  type IpcMainInvokeEvent,
} from 'electron';
import { buildContentSecurityPolicy, isAllowedNavigation } from './security';
import { isSafeFileName, isValidRefreshToken, MAX_SAVE_BYTES, SessionStore } from './session-store';

const SERVER_URL = process.env['MONITOREOTI_SERVER_URL'] ?? 'http://localhost:5126';
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

  window.once('ready-to-show', () => window.show());

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
  ipcMain.handle('clipboard:write', (event, text: unknown) => {
    fromPanel(event);
    if (typeof text !== 'string' || text.length === 0 || text.length > 4_000) throw new Error('Texto inválido.');
    clipboard.writeText(text);
  });
  ipcMain.handle('session:clear', (event) => {
    fromPanel(event);
    return sessions.clear();
  });
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    const [window] = BrowserWindow.getAllWindows();
    if (window) {
      if (window.isMinimized()) window.restore();
      window.focus();
    }
  });

  void app.whenReady().then(() => {
    applySessionSecurity();
    registerIpc();
    createMainWindow();

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createMainWindow();
    });
  });

  app.on('window-all-closed', () => {
    if (process.platform !== 'darwin') app.quit();
  });
}

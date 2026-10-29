/**
 * Modo UI: proceso Electron en la sesión del usuario. Reemplaza al SessionHelper:
 * en fases siguientes captura la pantalla, inyecta mouse/teclado durante una sesión
 * remota, muestra el aviso de sesión activa y el ícono de bandeja.
 * Se comunica con el modo servicio por named pipe (Fase 7).
 */
import { join } from 'node:path';
import { BrowserWindow, app, session } from 'electron';

function createStatusWindow(): void {
  const window = new BrowserWindow({
    width: 420,
    height: 260,
    resizable: false,
    title: 'Agente MonitoreoTI',
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  window.webContents.on('will-navigate', (event) => event.preventDefault());
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));

  const devUrl = app.isPackaged ? undefined : process.env['ELECTRON_RENDERER_URL'];
  if (devUrl) {
    void window.loadURL(devUrl);
  } else {
    void window.loadFile(join(__dirname, '../renderer/index.html'));
  }
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  void app.whenReady().then(() => {
    session.defaultSession.setPermissionRequestHandler((_webContents, _permission, callback) => callback(false));
    createStatusWindow();
  });

  app.on('window-all-closed', () => app.quit());
}

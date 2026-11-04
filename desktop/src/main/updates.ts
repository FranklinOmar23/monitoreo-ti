import type { BrowserWindow } from 'electron';
import { autoUpdater } from 'electron-updater';

/** Cada cuánto se busca una versión nueva (además de al abrir). */
const CHECK_EVERY_MS = 4 * 60 * 60 * 1000;
const FIRST_CHECK_MS = 15_000;

/** Ruta del servidor con latest.yml y los instaladores del panel (decisión 70). */
export function updateFeedUrl(serverUrl: string): string {
  return `${serverUrl.replace(/\/+$/, '')}/api/v1/panel/actualizacion`;
}

/**
 * Actualización automática desde nuestro servidor (sección 12.2): se descarga sola en segundo plano y el panel
 * ofrece reiniciar; si no, se instala al salir. Solo en la app instalada.
 */
export class PanelUpdates {
  private readyVersion: string | null = null;

  constructor(
    private readonly serverUrl: string,
    private readonly mainWindow: () => BrowserWindow | null,
  ) {}

  start(): void {
    autoUpdater.logger = null;
    autoUpdater.autoDownload = true;
    autoUpdater.autoInstallOnAppQuit = true;
    autoUpdater.setFeedURL({ provider: 'generic', url: updateFeedUrl(this.serverUrl) });

    autoUpdater.on('update-downloaded', (info) => {
      this.readyVersion = info.version;
      const window = this.mainWindow();
      if (window && !window.isDestroyed()) window.webContents.send('update:ready', info.version);
    });
    // Sin servidor o sin versión publicada: se vuelve a intentar en la próxima vuelta, sin molestar al técnico.
    autoUpdater.on('error', (error) => console.warn('Actualización del panel:', error.message));

    const check = () => void autoUpdater.checkForUpdates().catch(() => undefined);
    setTimeout(check, FIRST_CHECK_MS);
    setInterval(check, CHECK_EVERY_MS);
  }

  /** La versión ya descargada, si hay una (la ventana puede abrirse después del aviso). */
  ready(): string | null {
    return this.readyVersion;
  }

  install(): void {
    if (!this.readyVersion) return;
    // Instalación silenciosa y abre el panel otra vez al terminar.
    autoUpdater.quitAndInstall(true, true);
  }
}

// Contrato entre el preload y el renderer: lo único del proceso principal que ve la interfaz.

export interface AppInfo {
  name: string;
  version: string;
  serverUrl: string;
}

/** Refresh token guardado cifrado por el proceso principal (safeStorage). */
export interface SessionBridge {
  load(): Promise<string | null>;
  save(refreshToken: string): Promise<void>;
  clear(): Promise<void>;
}

/** saved: false sin error = el usuario canceló. */
export interface SaveFileResult {
  saved: boolean;
  path?: string;
  error?: string;
}

/** API que el preload expone al renderer en window.monitoreo. */
export interface MonitoreoBridge {
  getAppInfo(): Promise<AppInfo>;
  session: SessionBridge;
  /** Pide dónde guardar con el diálogo del sistema y guarda. */
  saveFile(name: string, data: Uint8Array): Promise<SaveFileResult>;
  /** Copia texto al portapapeles (el renderer no tiene permiso de portapapeles). */
  copyText(text: string): Promise<void>;
}

declare global {
  interface Window {
    monitoreo?: MonitoreoBridge;
  }
}

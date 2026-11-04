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

/** Lo que la ventana principal entrega para abrir una terminal en su propia ventana (token de un solo uso). */
export interface TerminalTicket {
  sessionId: number;
  /** terminal (W-30) o escritorio (W-28). */
  kind: 'terminal' | 'escritorio';
  machineId?: number;
  relayPath: string;
  token: string;
  machineName: string;
  quality?: number;
  clipboardSync?: boolean;
  viewOnly?: boolean;
  allowFiles?: boolean;
  technician?: string;
  branchName?: string;
  agentVersion?: string;
  lastReportAt?: string;
}

/** Lo que recibe la ventana de la terminal: su ticket y el servidor. */
export interface TerminalLaunch extends TerminalTicket {
  serverUrl: string;
}

/**
 * Lo que muestra una vista 3D en su propia ventana (7.6): la ventana principal, que tiene la sesión, carga los
 * gabinetes y se los manda (y se los vuelve a mandar cuando cambian). racks son RackItem y details RackDetail.
 */
export interface View3DSnapshot {
  /** sala-{id} o gabinete-{id}: una ventana por sala o gabinete. */
  key: string;
  title: string;
  /** Un solo gabinete (W-44): más cerca y con los nombres de los equipos. */
  single: boolean;
  racks: unknown[];
  details: unknown[];
}

/** Vistas 3D en ventanas aparte: la ventana principal las alimenta y la ventana 3D las muestra. */
export interface View3DBridge {
  /** Ventana principal: open abre la ventana (o la trae al frente); si no, solo actualiza la que siga abierta. */
  show(snapshot: View3DSnapshot, open: boolean): Promise<void>;
  /** Ventana principal: el técnico cerró esa ventana 3D. */
  onClosed(listener: (key: string) => void): () => void;
  /** Ventana principal: en una ventana 3D hicieron clic en un equipo. */
  onOpenDevice(listener: (deviceId: number) => void): () => void;
  /** Ventana 3D: los datos actuales. */
  data(): Promise<View3DSnapshot | null>;
  /** Ventana 3D: datos nuevos. */
  onData(listener: (snapshot: View3DSnapshot) => void): () => void;
  /** Ventana 3D: abrir ese equipo en la ventana principal. */
  openDevice(deviceId: number): Promise<void>;
}

/** Enlace monitoreoti:// ya validado por el proceso principal. */
export type DeepLink =
  { kind: 'maquina'; id: number } | { kind: 'qr'; code: string } | { kind: 'restablecer'; token: string };

/** Enlaces profundos: el proceso principal guarda el último y avisa; la ventana principal lo recoge una vez. */
export interface DeepLinkBridge {
  take(): Promise<DeepLink | null>;
  onOpen(listener: () => void): () => void;
}

/** Ícono de la bandeja: cuántas alertas críticas hay y a dónde ir desde su menú. */
export interface TrayBridge {
  setCriticalCount(count: number): Promise<void>;
  onNavigate(listener: (target: 'alertas' | 'buscar') => void): () => void;
}

/** Actualización del panel: la versión ya descargada y reiniciar para instalarla. */
export interface UpdateBridge {
  status(): Promise<string | null>;
  onReady(listener: (version: string) => void): () => void;
  install(): Promise<void>;
}

/** API que el preload expone al renderer en window.monitoreo. */
export interface MonitoreoBridge {
  getAppInfo(): Promise<AppInfo>;
  session: SessionBridge;
  /** Pide dónde guardar con el diálogo del sistema y guarda. */
  saveFile(name: string, data: Uint8Array): Promise<SaveFileResult>;
  /** Copia texto al portapapeles (el renderer no tiene permiso de portapapeles). */
  copyText(text: string): Promise<void>;
  /** Texto del portapapeles local (para pegarlo en el equipo remoto). */
  readText(): Promise<string>;
  /** Aviso nativo de Windows (alertas críticas). Al hacerle clic se trae la ventana y se avisa con onNotificationClick. */
  notify(title: string, body: string, alertId: number): Promise<void>;
  /** Clic en un aviso: la alerta a abrir. Devuelve la función para dejar de escuchar. */
  onNotificationClick(listener: (alertId: number) => void): () => void;
  /** Abre la terminal en una ventana aparte con el ticket ya pedido (W-30). */
  openTerminal(ticket: TerminalTicket): Promise<void>;
  /** En la ventana de la terminal: su ticket (se entrega una sola vez). */
  terminalTicket(): Promise<TerminalLaunch | null>;
  /** Desde una ventana de sesión: pedir a la ventana principal otra sesión del mismo equipo. */
  requestSession(type: 'archivos' | 'terminal' | 'escritorio'): Promise<boolean>;
  /** En la ventana principal: una ventana de sesión pidió otra sesión de su equipo. */
  onSessionRequest(
    listener: (request: {
      machineId: number;
      machineName: string;
      type: 'archivos' | 'terminal' | 'escritorio';
    }) => void,
  ): () => void;
  /** Archivos remotos (W-31): el disco local solo lo toca el proceso principal. */
  files: FileBridge;
  view3d: View3DBridge;
  deepLinks: DeepLinkBridge;
  tray: TrayBridge;
  updates: UpdateBridge;
}

/** Descargar a un archivo local elegido con el diálogo del sistema y subir uno elegido igual, por partes de 256 KB. */
export interface FileBridge {
  /** null si el técnico canceló el diálogo. */
  beginDownload(name: string): Promise<{ id: number } | null>;
  write(id: number, data: Uint8Array): Promise<void>;
  /** ok false borra lo descargado a medias. Devuelve dónde quedó. */
  endDownload(id: number, ok: boolean): Promise<{ path: string | null }>;
  /** null si el técnico canceló el diálogo. */
  pickUpload(): Promise<{ id: number; name: string; size: number } | null>;
  read(id: number, offset: number, length: number): Promise<Uint8Array>;
  endUpload(id: number): Promise<void>;
}

declare global {
  interface Window {
    monitoreo?: MonitoreoBridge;
  }
}

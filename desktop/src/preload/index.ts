import { contextBridge, ipcRenderer } from 'electron';
import type { DeepLink, MonitoreoBridge, View3DSnapshot } from './bridge';

const isId = (value: unknown): value is number => typeof value === 'number' && Number.isInteger(value) && value > 0;

// Solo se exponen funciones puntuales; nunca ipcRenderer completo.
const bridge: MonitoreoBridge = {
  getAppInfo: () => ipcRenderer.invoke('app:get-info'),
  saveFile: (name, data) => ipcRenderer.invoke('file:save', name, data),
  copyText: (text) => ipcRenderer.invoke('clipboard:write', text),
  readText: () => ipcRenderer.invoke('clipboard:read'),
  notify: (title, body, alertId) => ipcRenderer.invoke('notification:show', title, body, alertId),
  onNotificationClick: (listener) => {
    const handler = (_event: unknown, alertId: unknown) => {
      if (typeof alertId === 'number' && Number.isInteger(alertId)) listener(alertId);
    };
    ipcRenderer.on('notification:click', handler);
    return () => ipcRenderer.removeListener('notification:click', handler);
  },
  openTerminal: (ticket) => ipcRenderer.invoke('terminal:open', ticket),
  terminalTicket: () => ipcRenderer.invoke('terminal:ticket'),
  requestSession: (type) => ipcRenderer.invoke('remote:open-session', type),
  onSessionRequest: (listener) => {
    const handler = (_event: unknown, request: unknown) => {
      const r = request as { machineId?: unknown; machineName?: unknown; type?: unknown } | null;
      if (
        typeof r?.machineId === 'number' &&
        typeof r.machineName === 'string' &&
        (r.type === 'archivos' || r.type === 'terminal' || r.type === 'escritorio')
      )
        listener({ machineId: r.machineId, machineName: r.machineName, type: r.type });
    };
    ipcRenderer.on('remote:open-session', handler);
    return () => ipcRenderer.removeListener('remote:open-session', handler);
  },
  files: {
    beginDownload: (name) => ipcRenderer.invoke('download:begin', name),
    write: (id, data) => ipcRenderer.invoke('download:write', id, data),
    endDownload: (id, ok) => ipcRenderer.invoke('download:end', id, ok),
    pickUpload: () => ipcRenderer.invoke('upload:pick'),
    read: (id, offset, length) => ipcRenderer.invoke('upload:read', id, offset, length),
    endUpload: (id) => ipcRenderer.invoke('upload:end', id),
  },
  view3d: {
    show: (snapshot, open) => ipcRenderer.invoke('view3d:show', snapshot, open),
    onClosed: (listener) => {
      const handler = (_event: unknown, key: unknown) => {
        if (typeof key === 'string') listener(key);
      };
      ipcRenderer.on('view3d:closed', handler);
      return () => ipcRenderer.removeListener('view3d:closed', handler);
    },
    onOpenDevice: (listener) => {
      const handler = (_event: unknown, id: unknown) => {
        if (isId(id)) listener(id);
      };
      ipcRenderer.on('view3d:open-device', handler);
      return () => ipcRenderer.removeListener('view3d:open-device', handler);
    },
    data: () => ipcRenderer.invoke('view3d:data'),
    onData: (listener) => {
      const handler = (_event: unknown, snapshot: unknown) => {
        if (snapshot && typeof snapshot === 'object') listener(snapshot as View3DSnapshot);
      };
      ipcRenderer.on('view3d:data', handler);
      return () => ipcRenderer.removeListener('view3d:data', handler);
    },
    openDevice: (deviceId) => ipcRenderer.invoke('view3d:open-device', deviceId),
  },
  deepLinks: {
    take: () => ipcRenderer.invoke('deeplink:take') as Promise<DeepLink | null>,
    onOpen: (listener) => {
      const handler = () => listener();
      ipcRenderer.on('deeplink:open', handler);
      return () => ipcRenderer.removeListener('deeplink:open', handler);
    },
  },
  tray: {
    setCriticalCount: (count) => ipcRenderer.invoke('tray:alerts', count),
    onNavigate: (listener) => {
      const handler = (_event: unknown, target: unknown) => {
        if (target === 'alertas' || target === 'buscar') listener(target);
      };
      ipcRenderer.on('tray:navigate', handler);
      return () => ipcRenderer.removeListener('tray:navigate', handler);
    },
  },
  updates: {
    status: () => ipcRenderer.invoke('update:status'),
    onReady: (listener) => {
      const handler = (_event: unknown, version: unknown) => {
        if (typeof version === 'string' && version.length <= 50) listener(version);
      };
      ipcRenderer.on('update:ready', handler);
      return () => ipcRenderer.removeListener('update:ready', handler);
    },
    install: () => ipcRenderer.invoke('update:install'),
  },
  session: {
    load: () => ipcRenderer.invoke('session:load'),
    save: (refreshToken) => ipcRenderer.invoke('session:save', refreshToken),
    clear: () => ipcRenderer.invoke('session:clear'),
  },
};

contextBridge.exposeInMainWorld('monitoreo', bridge);

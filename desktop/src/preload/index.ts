import { contextBridge, ipcRenderer } from 'electron';
import type { MonitoreoBridge } from './bridge';

// Solo se exponen funciones puntuales; nunca ipcRenderer completo.
const bridge: MonitoreoBridge = {
  getAppInfo: () => ipcRenderer.invoke('app:get-info'),
  saveFile: (name, data) => ipcRenderer.invoke('file:save', name, data),
  copyText: (text) => ipcRenderer.invoke('clipboard:write', text),
  session: {
    load: () => ipcRenderer.invoke('session:load'),
    save: (refreshToken) => ipcRenderer.invoke('session:save', refreshToken),
    clear: () => ipcRenderer.invoke('session:clear'),
  },
};

contextBridge.exposeInMainWorld('monitoreo', bridge);

import type { CableType, DeviceStatus, DeviceType, MachineStatus, PortStatus, PortType } from '@monitoreoti/shared';

export const DEVICE_TYPE_LABEL: Record<DeviceType, string> = {
  switch: 'Switch',
  router: 'Router',
  firewall: 'Firewall',
  servidor: 'Servidor',
  patch_panel: 'Patch panel',
  ups: 'UPS',
  pdu: 'PDU',
  bandeja: 'Bandeja',
  organizador: 'Organizador',
  access_point: 'Access point',
  otro: 'Otro',
};

export const DEVICE_STATUS_LABEL: Record<DeviceStatus, string> = {
  en_linea: 'En línea',
  advertencia: 'Advertencia',
  critico: 'Crítico',
  sin_respuesta: 'Sin respuesta',
  pasivo: 'Pasivo',
  baja: 'De baja',
};

/** Color del semáforo para el badge del dispositivo. */
export const DEVICE_STATUS_TONE: Record<DeviceStatus, MachineStatus> = {
  en_linea: 'sano',
  advertencia: 'advertencia',
  critico: 'critico',
  sin_respuesta: 'desconectado',
  pasivo: 'desconectado',
  baja: 'desconectado',
};

export const PORT_STATUS_LABEL: Record<PortStatus, string> = {
  conectado: 'Conectado',
  libre: 'Libre',
  errores: 'Con errores',
  caido: 'Caído',
  deshabilitado: 'Deshabilitado',
};

export const PORT_TYPE_LABEL: Record<PortType, string> = {
  rj45: 'RJ45',
  sfp: 'SFP',
  sfp_plus: 'SFP+',
  fibra: 'Fibra',
  consola: 'Consola',
  otro: 'Otro',
};

export const CABLE_TYPE_LABEL: Record<CableType, string> = {
  cat5e: 'Cat 5e',
  cat6: 'Cat 6',
  cat6a: 'Cat 6A',
  cat7: 'Cat 7',
  fibra_mm: 'Fibra multimodo',
  fibra_sm: 'Fibra monomodo',
  dac: 'DAC',
  otro: 'Otro',
};

/** Color del puerto (sección 12.4): verde conectado, gris libre, amarillo con errores, rojo caído, azul troncal. */
export const portTone = (port: { status: PortStatus; isTrunk: boolean }) =>
  port.isTrunk && port.status === 'conectado' ? 'troncal' : port.status;

export const keysOf = <T extends string>(record: Record<T, string>) => Object.keys(record) as T[];

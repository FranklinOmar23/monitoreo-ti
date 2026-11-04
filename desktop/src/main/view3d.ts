/** Datos de una vista 3D en su propia ventana (7.6): los manda la ventana principal y se reenvían a la ventana 3D. */
export interface View3DSnapshot {
  key: string;
  title: string;
  single: boolean;
  racks: unknown[];
  details: unknown[];
}

/** Una sala grande cabe de sobra; más es un error o un abuso (todo pasa por el proceso principal). */
export const MAX_VIEW3D_BYTES = 2 * 1024 * 1024;
const MAX_RACKS = 200;

/** null si no tiene la forma esperada. Lo de adentro (gabinetes y equipos) solo lo dibuja la ventana 3D. */
export function parseView3DSnapshot(value: unknown): View3DSnapshot | null {
  if (!value || typeof value !== 'object') return null;
  const v = value as Record<string, unknown>;
  if (typeof v['key'] !== 'string' || !/^(sala|gabinete)-\d{1,9}$/.test(v['key'])) return null;
  if (typeof v['title'] !== 'string' || v['title'].length === 0 || v['title'].length > 200) return null;
  if (typeof v['single'] !== 'boolean') return null;
  const racks = v['racks'];
  const details = v['details'];
  if (!Array.isArray(racks) || !Array.isArray(details) || racks.length > MAX_RACKS || details.length > MAX_RACKS)
    return null;
  if (JSON.stringify(value).length > MAX_VIEW3D_BYTES) return null;
  return { key: v['key'], title: v['title'], single: v['single'], racks, details };
}

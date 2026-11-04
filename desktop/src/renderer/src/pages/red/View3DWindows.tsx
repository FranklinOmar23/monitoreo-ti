import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { useQueries, useQuery } from '@tanstack/react-query';
import type { RackDetail, RackItem } from '@monitoreoti/shared';
import { usePanelApi } from '../../data/PanelData';

/** Lo que se abre en su propia ventana: una sala completa o un gabinete. */
export interface View3DTarget {
  kind: 'sala' | 'gabinete';
  id: number;
  title: string;
}

const OpenContext = createContext<((target: View3DTarget) => void) | null>(null);

/** Abre la vista 3D en su ventana; null fuera de la aplicación de escritorio (no hay ventanas aparte). */
export function useOpenView3D(): ((target: View3DTarget) => void) | null {
  return useContext(OpenContext);
}

const keyOf = (target: View3DTarget) => `${target.kind}-${target.id}`;

/**
 * Vistas 3D en ventanas aparte (7.6). La ventana 3D no tiene sesión: la principal carga los gabinetes de cada ventana
 * abierta y se los manda por el proceso principal, otra vez cada vez que cambian (los avisos en vivo los recargan).
 * Va en la raíz del panel para que siga alimentándolas aunque el técnico cambie de pantalla.
 */
export function View3DWindows({ children }: { children: ReactNode }) {
  const bridge = typeof window === 'undefined' ? undefined : window.monitoreo?.view3d;
  // nonce: cada pedido de abrir (aunque ya esté abierta) la trae al frente.
  const [open, setOpen] = useState<{ target: View3DTarget; nonce: number }[]>([]);
  const counter = useRef(0);

  useEffect(() => bridge?.onClosed((key) => setOpen((list) => list.filter((o) => keyOf(o.target) !== key))), [bridge]);

  const show = useCallback((target: View3DTarget) => {
    const nonce = ++counter.current;
    setOpen((list) => [...list.filter((o) => keyOf(o.target) !== keyOf(target)), { target, nonce }]);
  }, []);

  return (
    <OpenContext.Provider value={bridge ? show : null}>
      {children}
      {open.map((o) => (
        <Feed key={keyOf(o.target)} target={o.target} nonce={o.nonce} />
      ))}
    </OpenContext.Provider>
  );
}

function Feed({ target, nonce }: { target: View3DTarget; nonce: number }) {
  const api = usePanelApi();
  const room = target.kind === 'sala';
  const roomRacks = useQuery({
    queryKey: ['red', 'gabinetes', target.id],
    queryFn: () => api.red.racks(target.id),
    enabled: room,
  });
  const rack = useQuery({
    queryKey: ['red', 'gabinete', target.id],
    queryFn: () => api.red.rack(target.id),
    enabled: !room,
  });
  const roomDetails = useQueries({
    queries: (room ? (roomRacks.data ?? []) : []).map((r) => ({
      queryKey: ['red', 'gabinete', r.id],
      queryFn: () => api.red.rack(r.id),
    })),
  });

  const racks: RackItem[] | undefined = room ? roomRacks.data : rack.data && [rack.data.rack];
  const details: RackDetail[] = room
    ? roomDetails.flatMap((d) => (d.data ? [d.data] : []))
    : rack.data
      ? [rack.data]
      : [];
  const ready = racks !== undefined && (!room || roomDetails.every((d) => !d.isPending));
  // Solo se manda si algo cambió (useQueries devuelve otra lista en cada render).
  const payload = ready ? JSON.stringify({ racks, details }) : null;
  const sentNonce = useRef(0);

  useEffect(() => {
    const bridge = window.monitoreo?.view3d;
    if (!bridge || payload === null) return;
    const openIt = sentNonce.current !== nonce;
    sentNonce.current = nonce;
    const data = JSON.parse(payload) as { racks: RackItem[]; details: RackDetail[] };
    void bridge
      .show({ key: keyOf(target), title: target.title, single: !room, ...data }, openIt)
      .catch(() => undefined);
  }, [payload, nonce, target, room]);

  return null;
}

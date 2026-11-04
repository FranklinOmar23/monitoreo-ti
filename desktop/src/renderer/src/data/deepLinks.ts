import { useEffect, useRef } from 'react';
import type { DeepLink, DeepLinkBridge } from '../../../preload/bridge';

/** Devuelve true si atendió el enlace; si nadie lo atiende, espera (por ejemplo, a que el técnico inicie sesión). */
type Handler = (link: DeepLink) => boolean;

let pending: DeepLink | null = null;
const handlers = new Set<{ current: Handler }>();

function deliver(): void {
  if (!pending) return;
  for (const handler of handlers) {
    if (handler.current(pending)) {
      pending = null;
      return;
    }
  }
}

/** Recibe los enlaces monitoreoti:// de la ventana principal (al abrir y cada vez que llega uno). */
export function startDeepLinks(bridge: DeepLinkBridge | undefined): () => void {
  if (!bridge) return () => undefined;
  const pull = () =>
    void bridge
      .take()
      .then((link) => {
        if (!link) return;
        pending = link;
        deliver();
      })
      .catch(() => undefined);
  pull();
  return bridge.onOpen(pull);
}

/** Atiende los enlaces mientras el componente está montado y enabled es true. */
export function useDeepLink(handler: Handler, enabled = true): void {
  const ref = useRef(handler);
  useEffect(() => {
    ref.current = handler;
  });
  useEffect(() => {
    if (!enabled) return;
    const entry = { current: (link: DeepLink) => ref.current(link) };
    handlers.add(entry);
    deliver();
    return () => {
      handlers.delete(entry);
    };
  }, [enabled]);
}

/** Solo para las pruebas: simula un enlace que llega. */
export function receiveDeepLinkForTest(link: DeepLink | null): void {
  pending = link;
  deliver();
}

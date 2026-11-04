import type { TerminalLaunch } from '../../../../preload/bridge';

/**
 * El proceso principal entrega el ticket de una ventana de sesión (terminal o escritorio) una sola vez: se pide una vez
 * por ventana y se reutiliza (React, en modo estricto, monta los efectos dos veces en desarrollo y el segundo ya no lo
 * recibiría).
 */
let ticketOnce: Promise<TerminalLaunch | null> | null = null;

export function loadWindowTicket(): Promise<TerminalLaunch | null> {
  ticketOnce ??= window.monitoreo?.terminalTicket() ?? Promise.resolve(null);
  return ticketOnce;
}

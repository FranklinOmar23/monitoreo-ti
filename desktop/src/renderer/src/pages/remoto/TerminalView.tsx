import { useEffect, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';

export type TerminalStatus =
  | { kind: 'iniciando' }
  | { kind: 'lista'; shell: string }
  | { kind: 'fin'; code: number | null }
  | { kind: 'error'; message: string };

interface TerminalViewProps {
  /** Socket del relay ya conectado con el equipo. */
  socket: WebSocket;
  shell?: 'powershell' | 'cmd';
  onStatus?: (status: TerminalStatus) => void;
}

/**
 * W-30 Terminal: xterm.js sobre el relay. Lo que escribe el técnico va en binario (UTF-8); la salida del equipo llega
 * en binario y se dibuja tal cual (secuencias VT de ConPTY). El tamaño de la ventana se manda al cambiar.
 */
export function TerminalView({ socket, shell = 'powershell', onStatus }: TerminalViewProps) {
  const container = useRef<HTMLDivElement>(null);
  const statusRef = useRef(onStatus);
  const [ended, setEnded] = useState(false);

  useEffect(() => {
    statusRef.current = onStatus;
  }, [onStatus]);

  useEffect(() => {
    const element = container.current;
    if (!element) return;

    const terminal = new Terminal({
      cursorBlink: true,
      fontFamily: "'Cascadia Mono', Consolas, 'Courier New', monospace",
      fontSize: 13,
      scrollback: 5000,
      theme: { background: '#0b1f26', foreground: '#e6efe9', cursor: '#5eead4' },
    });
    const fit = new FitAddon();
    terminal.loadAddon(fit);
    terminal.open(element);
    fit.fit();
    terminal.focus();

    const encoder = new TextEncoder();
    const send = (data: string | Uint8Array<ArrayBuffer>) => {
      if (socket.readyState === WebSocket.OPEN) socket.send(data);
    };
    const report = (status: TerminalStatus) => statusRef.current?.(status);

    report({ kind: 'iniciando' });
    send(JSON.stringify({ tipo: 'iniciar', shell, columnas: terminal.cols, filas: terminal.rows }));

    const typing = terminal.onData((data) => send(encoder.encode(data)));
    const resizing = terminal.onResize(({ cols, rows }) =>
      send(JSON.stringify({ tipo: 'tamano', columnas: cols, filas: rows })),
    );
    const observer = new ResizeObserver(() => {
      try {
        fit.fit();
      } catch {
        // La ventana se está cerrando.
      }
    });
    observer.observe(element);

    const onMessage = (event: MessageEvent) => {
      if (event.data instanceof ArrayBuffer) {
        terminal.write(new Uint8Array(event.data));
        return;
      }
      if (typeof event.data !== 'string') return;
      let message: Record<string, unknown>;
      try {
        message = JSON.parse(event.data) as Record<string, unknown>;
      } catch {
        return;
      }
      if (message['tipo'] === 'listo') report({ kind: 'lista', shell: String(message['shell'] ?? shell) });
      else if (message['tipo'] === 'fin') {
        const code = typeof message['codigo'] === 'number' ? message['codigo'] : null;
        terminal.write(`\r\n\x1b[90m[El shell terminó${code === null ? '' : ` con código ${code}`}.]\x1b[0m\r\n`);
        setEnded(true);
        report({ kind: 'fin', code });
      } else if (message['tipo'] === 'error') {
        const text = String(message['mensaje'] ?? 'Error en la terminal.');
        terminal.write(`\r\n\x1b[31m${text}\x1b[0m\r\n`);
        setEnded(true);
        report({ kind: 'error', message: text });
      } else if (message['relay'] === 'cerrada') {
        terminal.write(`\r\n\x1b[90m[Sesión cerrada: ${String(message['motivo'] ?? '')}.]\x1b[0m\r\n`);
        setEnded(true);
      }
    };

    socket.addEventListener('message', onMessage);

    return () => {
      socket.removeEventListener('message', onMessage);
      typing.dispose();
      resizing.dispose();
      observer.disconnect();
      terminal.dispose();
    };
  }, [socket, shell]);

  return <div ref={container} className="terminal-view" data-ended={ended} aria-label="Terminal remota" />;
}

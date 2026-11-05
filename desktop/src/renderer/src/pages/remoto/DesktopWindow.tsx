import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Icon, type IconName } from '@components/Icon';
import type { TerminalLaunch } from '../../../../preload/bridge';
import { timeAgo } from '../../data/format';
import { DesktopView, type DesktopEvent, type DesktopStats, type FitMode, type RemoteMonitor } from './DesktopView';
import { QUALITIES, SHORTCUTS } from './desktopOptions';
import { safeLocalName } from './fileClient';
import { FilePanel } from './FilePanel';
import { browserSocket, connectRelay, type RelaySocketFactory } from './RemoteSession';
import { loadWindowTicket } from './windowTicket';

/** La barra se esconde tras este tiempo sin el mouse encima (y al empezar, tras un poco más). */
const TOOLBAR_HIDE_MS = 1500;
const TOOLBAR_FIRST_HIDE_MS = 3000;
const PINNED_KEY = 'centinela.escritorio.barraFija';

function readPinned(): boolean {
  try {
    return localStorage.getItem(PINNED_KEY) === '1';
  } catch {
    return false;
  }
}

function savePinned(value: boolean): void {
  try {
    localStorage.setItem(PINNED_KEY, value ? '1' : '0');
  } catch {
    // Sin almacenamiento local la preferencia dura solo esta sesión.
  }
}

type Phase =
  | { kind: 'cargando' }
  | { kind: 'conectando' }
  | { kind: 'sesion'; socket: WebSocket }
  | { kind: 'error'; code: string; message: string; at: Date };

/** Causas probables de cada error, en palabras del técnico (W-29b). */
export function errorCauses(
  code: string,
  branch: string | undefined,
  user: string | null,
): { icon: IconName; text: string }[] {
  if (code === 'PERMISO_DENEGADO')
    return [
      { icon: 'user', text: `${user ?? 'El usuario'} rechazó la conexión o no estaba frente al equipo.` },
      { icon: 'clock', text: 'La solicitud vence a los 30 segundos sin respuesta.' },
    ];
  if (code === 'ERR_RELAY_TIMEOUT')
    return [
      { icon: 'power', text: 'El equipo puede estar apagado o suspendido.' },
      { icon: 'wifiOff', text: `La red de la sucursal${branch ? ` ${branch}` : ''} puede tener problemas.` },
      { icon: 'shield', text: 'Un firewall local puede estar bloqueando el agente.' },
    ];
  return [];
}

function clock(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  return [h, m, s].map((n) => String(n).padStart(2, '0')).join(':');
}

interface DesktopWindowProps {
  loadTicket?: () => Promise<TerminalLaunch | null>;
  createSocket?: RelaySocketFactory;
}

/**
 * Ventana propia del escritorio remoto (W-28, W-29): la ventana principal pidió la sesión (permiso, 2FA, opciones de
 * W-28a) y aquí se conecta al relay con el token de un solo uso. Pasos de conexión, error con sus causas, barra flotante
 * (portapapeles, teclas, ajuste, calidad, archivos, captura, terminal, más opciones) y barra de estado.
 */
export function DesktopWindow({ loadTicket = loadWindowTicket, createSocket = browserSocket }: DesktopWindowProps) {
  const [phase, setPhase] = useState<Phase>({ kind: 'cargando' });
  const [launch, setLaunch] = useState<TerminalLaunch | null>(null);
  const [permission, setPermission] = useState<{ user: string; until: number } | null>(null);
  const [ready, setReady] = useState<{ monitors: RemoteMonitor[]; user: string | null } | null>(null);
  const [streaming, setStreaming] = useState<number | null>(null);
  const [monitor, setMonitor] = useState(0);
  const [quality, setQuality] = useState(0);
  const [fit, setFit] = useState<FitMode>('ajustar');
  const [viewOnly, setViewOnly] = useState(false);
  const [stats, setStats] = useState<DesktopStats>({ latency: null, fps: 0, mbps: 0 });
  const [menu, setMenu] = useState<string | null>(null);
  const [filesOpen, setFilesOpen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [secureDesktop, setSecureDesktop] = useState<string | null>(null);
  const [inputBlocked, setInputBlocked] = useState(false);
  const [blackScreen, setBlackScreen] = useState(false);
  const [clipboardSync, setClipboardSync] = useState(false);
  const [remoteClipboard, setRemoteClipboard] = useState('');
  const [textToSend, setTextToSend] = useState('');
  const [size, setSize] = useState('—');
  // Barra que se oculta sola (se recuerda si el técnico la prefiere fija).
  const [pinned, setPinned] = useState(() => readPinned());
  const [revealed, setRevealed] = useState(true);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const showToolbar = () => {
    if (hideTimer.current) clearTimeout(hideTimer.current);
    setRevealed(true);
  };
  const hideSoon = (ms = TOOLBAR_HIDE_MS) => {
    if (hideTimer.current) clearTimeout(hideTimer.current);
    hideTimer.current = setTimeout(() => setRevealed(false), ms);
  };
  const togglePinned = () => {
    const next = !pinned;
    setPinned(next);
    savePinned(next);
    if (!next) hideSoon();
  };
  // Al empezar a ver la pantalla, la barra se muestra un momento y se esconde (si no está fija).
  useEffect(() => {
    if (!streaming || pinned) return;
    hideTimer.current = setTimeout(() => setRevealed(false), TOOLBAR_FIRST_HIDE_MS);
    return () => {
      if (hideTimer.current) clearTimeout(hideTimer.current);
    };
  }, [streaming, pinned]);
  const toolbarHidden = !pinned && !revealed && menu === null;
  const [now, setNow] = useState(() => Date.now());
  const socketRef = useRef<WebSocket | null>(null);
  const canvasEl = useRef<HTMLCanvasElement | null>(null);
  const lastLocalClipboard = useRef('');
  const loader = useRef(loadTicket);
  const root = useRef<HTMLDivElement>(null);
  const bridge = window.monitoreo;

  useEffect(() => {
    let cancelled = false;
    const fail = (code: string, message: string) => setPhase({ kind: 'error', code, message, at: new Date() });
    void loader.current().then((ticket) => {
      if (cancelled) return;
      if (!ticket) {
        fail('ERR_SIN_SESION', 'Esta ventana ya no tiene una sesión. Ábrala de nuevo desde la ficha del equipo.');
        return;
      }
      setLaunch(ticket);
      setQuality(ticket.quality ?? 0);
      setViewOnly(ticket.viewOnly === true);
      setClipboardSync(ticket.clipboardSync === true);
      document.title = `Escritorio · ${ticket.machineName}`;
      setPhase({ kind: 'conectando' });
      socketRef.current = connectRelay(ticket.serverUrl, ticket, createSocket, {
        onConnected: (socket) => setPhase({ kind: 'sesion', socket }),
        onFail: (message) => fail('ERR_AGENTE', message),
        onClosed: (connected) =>
          setPhase((p) =>
            p.kind === 'error'
              ? p
              : connected
                ? { kind: 'error', code: 'ERR_SESION_CERRADA', message: 'La sesión terminó.', at: new Date() }
                : {
                    kind: 'error',
                    code: 'ERR_RELAY_TIMEOUT',
                    message: 'El agente no respondió en 60 segundos.',
                    at: new Date(),
                  },
          ),
      });
    });
    return () => {
      cancelled = true;
      socketRef.current?.close();
    };
  }, [createSocket]);

  // Reloj de la sesión y de la espera del permiso.
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  // Un aviso breve en la barra.
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(null), 4000);
    return () => clearTimeout(timer);
  }, [notice]);

  const send = useCallback((message: Record<string, unknown>) => {
    const socket = socketRef.current;
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
  }, []);

  const combo = (keys: string[]) => {
    for (const code of keys) send({ tipo: 'tecla', codigo: code, abajo: true });
    for (const code of [...keys].reverse()) send({ tipo: 'tecla', codigo: code, abajo: false });
  };

  const onEvent = useCallback(
    (event: DesktopEvent) => {
      switch (event.kind) {
        case 'permiso':
          setPermission({ user: event.user, until: Date.now() + event.seconds * 1000 });
          break;
        case 'listo':
          setReady({ monitors: event.monitors, user: event.user });
          setMonitor(event.monitor);
          break;
        case 'primera_imagen':
          setStreaming(Date.now());
          break;
        case 'fin':
          setPhase({ kind: 'error', code: event.code, message: event.message, at: new Date() });
          socketRef.current?.close();
          break;
        case 'escritorio':
          setSecureDesktop(event.label);
          break;
        case 'aviso':
          setNotice(event.message);
          break;
        case 'portapapeles':
          setRemoteClipboard(event.text);
          if (event.automatic) {
            lastLocalClipboard.current = event.text;
            void bridge?.copyText(event.text.slice(0, 4000));
          }
          break;
        case 'modo':
          setViewOnly(event.viewOnly);
          break;
        case 'entrada_bloqueada':
          setInputBlocked(event.active);
          break;
        case 'pantalla_negra':
          setBlackScreen(event.active);
          if (event.message) setNotice(event.message);
          break;
      }
    },
    [bridge],
  );

  const onStats = useCallback((value: DesktopStats) => {
    setStats(value);
    const canvas = canvasEl.current;
    if (canvas?.width) setSize(`${canvas.width} × ${canvas.height}`);
  }, []);

  // Las opciones elegidas antes de conectar (W-28a) se aplican en cuanto el equipo está listo.
  const applied = useRef(false);
  useEffect(() => {
    if (!ready || applied.current) return;
    applied.current = true;
    send({ tipo: 'calidad', valor: quality });
    send({ tipo: 'sincronizar', activo: clipboardSync });
  }, [ready, quality, clipboardSync, send]);

  // Portapapeles sincronizado: lo que se copia en esta PC llega al equipo.
  useEffect(() => {
    if (!clipboardSync || !bridge || phase.kind !== 'sesion' || viewOnly) return;
    const timer = setInterval(() => {
      if (!document.hasFocus()) return;
      void bridge.readText().then((text) => {
        if (text && text !== lastLocalClipboard.current) {
          lastLocalClipboard.current = text;
          send({ tipo: 'portapapeles', texto: text });
        }
      });
    }, 1500);
    return () => clearInterval(timer);
  }, [clipboardSync, bridge, phase.kind, viewOnly, send]);

  // F11: pantalla completa del visor.
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.code !== 'F11') return;
      event.preventDefault();
      void (document.fullscreenElement ? document.exitFullscreen() : root.current?.requestFullscreen());
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const finish = () => {
    socketRef.current?.close();
    window.close();
  };

  const fullscreen = () =>
    void (document.fullscreenElement ? document.exitFullscreen() : root.current?.requestFullscreen());

  const screenshot = () => {
    const canvas = canvasEl.current;
    const files = bridge?.files;
    if (!canvas || !files || !launch) return;
    canvas.toBlob((blob) => {
      if (!blob) return;
      void blob.arrayBuffer().then(async (buffer) => {
        const stamp = new Date().toISOString().slice(0, 16).replace('T', ' ').replace(':', '-');
        const target = await files.beginDownload(safeLocalName(`Captura ${launch.machineName} ${stamp}.png`));
        if (!target) return;
        const bytes = new Uint8Array(buffer);
        for (let offset = 0; offset < bytes.length; offset += 256 * 1024)
          await files.write(target.id, bytes.subarray(offset, offset + 256 * 1024));
        const saved = await files.endDownload(target.id, true);
        setNotice(saved.path ? 'Captura guardada.' : 'No se pudo guardar la captura.');
      });
    }, 'image/png');
  };

  const confirmThen = (question: string, action: () => void) => {
    setMenu(null);
    if (window.confirm(question)) action();
  };

  const secure = launch?.serverUrl.startsWith('https') ?? false;

  if (phase.kind !== 'sesion') {
    return (
      <div className="desktop-window desktop-centered" ref={root}>
        {phase.kind === 'error' ? (
          <ConnectionError
            machine={launch?.machineName ?? 'el equipo'}
            code={phase.code}
            message={phase.message}
            at={phase.at}
            lastReportAt={launch?.lastReportAt}
            causes={errorCauses(phase.code, launch?.branchName, permission?.user ?? null)}
            onClose={() => window.close()}
            onRetry={
              bridge && launch?.machineId
                ? () => void bridge.requestSession('escritorio').then((ok) => ok && window.close())
                : undefined
            }
          />
        ) : (
          <ConnectingSteps
            machine={launch?.machineName ?? '…'}
            agentVersion={launch?.agentVersion}
            channel={false}
            permission={null}
            secure={secure}
            onCancel={finish}
          />
        )}
      </div>
    );
  }

  const elapsed = streaming ? Math.floor((now - streaming) / 1000) : 0;
  const qualityLabel = QUALITIES.find((q) => q.value === quality)?.label.toLowerCase() ?? 'automática';

  return (
    <div className="desktop-window" ref={root}>
      <div className="desktop-main">
        <div className="desktop-area">
          <DesktopView
            socket={phase.socket}
            onEvent={onEvent}
            onStats={onStats}
            fit={fit}
            viewOnly={viewOnly}
            canvasRef={(c) => (canvasEl.current = c)}
          />
          {!streaming && (
            <div className="desktop-overlay">
              <ConnectingSteps
                machine={launch?.machineName ?? '…'}
                agentVersion={launch?.agentVersion}
                channel
                permission={
                  permission
                    ? { user: permission.user, left: Math.max(0, Math.ceil((permission.until - now) / 1000)) }
                    : null
                }
                secure={secure}
                onCancel={finish}
              />
            </div>
          )}

          {streaming && !pinned && (
            <>
              {/* Llevar el mouse al borde de arriba la muestra. */}
              <div className="desktop-toolbar-edge" aria-hidden="true" onPointerEnter={showToolbar} />
              <button
                type="button"
                className="desktop-toolbar-handle"
                data-hidden={toolbarHidden || undefined}
                aria-label="Mostrar la barra de controles"
                tabIndex={toolbarHidden ? 0 : -1}
                onClick={showToolbar}
                onPointerEnter={showToolbar}
              >
                <Icon name="chevronDown" size={14} />
                <span className="mono">{launch?.machineName}</span>
              </button>
            </>
          )}

          {streaming && (
            <div
              className="desktop-toolbar"
              role="toolbar"
              aria-label="Controles del escritorio remoto"
              data-hidden={toolbarHidden || undefined}
              onPointerEnter={showToolbar}
              onPointerLeave={() => !pinned && hideSoon()}
              onFocus={showToolbar}
              onBlur={() => !pinned && hideSoon()}
            >
              <div className="toolbar-id">
                <strong>{launch?.machineName}</strong>
                <span className="mono">{clock(elapsed)}</span>
              </div>
              <ToolButton
                icon="keyboard"
                label="Enviar Ctrl+Alt+Supr"
                text="Ctrl+Alt+Supr"
                disabled={viewOnly}
                onClick={() => send({ tipo: 'cad' })}
              />
              <Menu
                id="portapapeles"
                icon="clipboard"
                label="Portapapeles"
                open={menu}
                setOpen={setMenu}
                disabled={viewOnly}
              >
                <label className="menu-toggle">
                  <span>Sincronizar portapapeles</span>
                  <input
                    type="checkbox"
                    checked={clipboardSync}
                    onChange={(e) => {
                      setClipboardSync(e.target.checked);
                      send({ tipo: 'sincronizar', activo: e.target.checked });
                    }}
                  />
                </label>
                <p className="menu-label">Último copiado en {launch?.machineName}</p>
                <div className="menu-box mono">{remoteClipboard || 'Nada todavía.'}</div>
                <button type="button" className="menu-link" onClick={() => send({ tipo: 'leer_portapapeles' })}>
                  Traer lo copiado en el equipo
                </button>
                <p className="menu-label">Enviar texto al equipo</p>
                <input
                  className="menu-input mono"
                  aria-label="Texto para enviar al equipo"
                  value={textToSend}
                  placeholder="Escriba o pegue el texto"
                  onChange={(e) => setTextToSend(e.target.value)}
                />
                <div className="menu-actions">
                  <button
                    type="button"
                    className="btn btn-outline"
                    disabled={!textToSend}
                    onClick={() => send({ tipo: 'escribir', texto: textToSend })}
                  >
                    Escribirlo
                  </button>
                  <button
                    type="button"
                    className="btn btn-primary"
                    disabled={!textToSend}
                    onClick={() => {
                      send({ tipo: 'portapapeles', texto: textToSend });
                      combo(['ControlLeft', 'KeyV']);
                    }}
                  >
                    Pegar
                  </button>
                </div>
              </Menu>
              <Menu
                id="teclas"
                icon="keyboard"
                label="Teclas especiales"
                open={menu}
                setOpen={setMenu}
                disabled={viewOnly}
              >
                <p className="menu-label">Enviar al equipo</p>
                {SHORTCUTS.map((s) => (
                  <button key={s.label} type="button" className="menu-item" onClick={() => combo(s.keys)}>
                    {s.label}
                  </button>
                ))}
              </Menu>
              <Menu id="ajustar" icon="monitor" label="Ajustar pantalla" text="Ajustar" open={menu} setOpen={setMenu}>
                <p className="menu-label">Ajuste</p>
                {(
                  [
                    ['ajustar', 'Ajustar a la ventana'],
                    ['real', 'Tamaño real (100 %)'],
                    ['estirar', 'Estirar'],
                  ] as const
                ).map(([value, label]) => (
                  <button
                    key={value}
                    type="button"
                    className="menu-item"
                    aria-pressed={fit === value}
                    onClick={() => setFit(value)}
                  >
                    {label}
                    {fit === value && <Icon name="check" size={14} />}
                  </button>
                ))}
                {ready && ready.monitors.length > 1 && (
                  <>
                    <p className="menu-label">Monitor remoto</p>
                    <div className="monitor-tiles">
                      {[
                        ...ready.monitors.map((m) => ({
                          index: m.indice,
                          top: String(m.indice + 1),
                          bottom: `${m.ancho} × ${m.alto}`,
                        })),
                        { index: -1, top: ready.monitors.map((m) => m.indice + 1).join('+'), bottom: 'Todos' },
                      ].map((tile) => (
                        <button
                          key={tile.index}
                          type="button"
                          className="monitor-tile"
                          aria-pressed={monitor === tile.index}
                          onClick={() => {
                            setMonitor(tile.index);
                            send({ tipo: 'monitor', indice: tile.index });
                          }}
                        >
                          <span className="monitor-screen">{tile.top}</span>
                          <span>{tile.bottom}</span>
                        </button>
                      ))}
                    </div>
                  </>
                )}
                <button type="button" className="menu-item" onClick={fullscreen}>
                  Pantalla completa <kbd>F11</kbd>
                </button>
              </Menu>
              <Menu
                id="calidad"
                icon="gauge"
                label="Calidad de imagen"
                text={quality === 0 ? 'Auto' : QUALITIES.find((q) => q.value === quality)?.label}
                open={menu}
                setOpen={setMenu}
              >
                <p className="menu-label">Calidad de imagen</p>
                {QUALITIES.map((q) => (
                  <button
                    key={q.value}
                    type="button"
                    className="menu-item menu-item-tall"
                    aria-pressed={quality === q.value}
                    onClick={() => {
                      setQuality(q.value);
                      send({ tipo: 'calidad', valor: q.value });
                    }}
                  >
                    <span>
                      <strong>{q.label}</strong>
                      <span className="muted">{q.hint}</span>
                    </span>
                    {quality === q.value && <Icon name="check" size={14} />}
                  </button>
                ))}
                <div className="menu-stats">
                  <div>
                    <span>Latencia</span>
                    <strong className="mono">{stats.latency === null ? '—' : `${stats.latency} ms`}</strong>
                  </div>
                  <div>
                    <span>Cuadros</span>
                    <strong className="mono">{stats.fps} fps</strong>
                  </div>
                  <div>
                    <span>Ancho</span>
                    <strong className="mono">{stats.mbps} Mb/s</strong>
                  </div>
                </div>
                {stats.codec && (
                  <p className="menu-codec muted">
                    {stats.codec === 'h264'
                      ? 'Video H.264 (pantalla V2): fluido y con poco consumo.'
                      : 'Imágenes JPEG (pantalla V1): el equipo no admite video o se ven todos los monitores.'}
                  </p>
                )}
              </Menu>
              <ToolButton
                icon="transfer"
                label="Transferir archivos"
                text="Archivos"
                pressed={filesOpen}
                disabled={!launch?.allowFiles}
                title={launch?.allowFiles ? 'Transferir archivos' : 'Necesita el permiso de archivos remotos'}
                onClick={() => setFilesOpen((v) => !v)}
              />
              <ToolButton icon="camera" label="Captura de pantalla" disabled={!bridge} onClick={screenshot} />
              <ToolButton
                icon="terminal"
                label="Abrir terminal"
                disabled={!bridge || !launch?.machineId}
                onClick={() => void bridge?.requestSession('terminal')}
              />
              <Menu id="mas" icon="dots" label="Más opciones" open={menu} setOpen={setMenu} align="right">
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  aria-pressed={inputBlocked}
                  onClick={() => send({ tipo: 'bloquear_entrada', activo: !inputBlocked })}
                >
                  <Icon name="lock" size={15} />
                  {inputBlocked ? 'Devolver teclado y mouse al usuario' : 'Bloquear teclado y mouse del usuario'}
                </button>
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  aria-pressed={blackScreen}
                  onClick={() => send({ tipo: 'pantalla_negra', activo: !blackScreen })}
                >
                  <Icon name="eyeOff" size={15} />
                  {blackScreen ? 'Mostrarle la pantalla al usuario' : 'Pantalla en negro para el usuario'}
                </button>
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  onClick={() => combo(['MetaLeft', 'KeyE'])}
                >
                  <Icon name="folder" size={15} /> Explorador de archivos
                </button>
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  onClick={() => combo(['ControlLeft', 'ShiftLeft', 'Escape'])}
                >
                  <Icon name="gauge" size={15} /> Administrador de tareas <kbd>Ctrl+Shift+Esc</kbd>
                </button>
                <hr />
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  onClick={() =>
                    confirmThen(
                      '¿Cambiar de usuario de Windows? La sesión del usuario queda abierta y se ve la pantalla de inicio.',
                      () => send({ tipo: 'accion', accion: 'cambiar_usuario' }),
                    )
                  }
                >
                  <Icon name="userSwitch" size={15} /> Cambiar de usuario de Windows
                </button>
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  onClick={() =>
                    confirmThen('¿Cerrar la sesión de Windows del usuario? Se pierde lo que no haya guardado.', () =>
                      send({ tipo: 'accion', accion: 'cerrar_sesion' }),
                    )
                  }
                >
                  <Icon name="logout" size={15} /> Cerrar sesión de Windows
                </button>
                <button
                  type="button"
                  className="menu-item menu-danger"
                  disabled={viewOnly}
                  onClick={() =>
                    confirmThen('¿Reiniciar el equipo? Se avisa al usuario y se reinicia en 15 segundos.', () =>
                      send({ tipo: 'accion', accion: 'reiniciar' }),
                    )
                  }
                >
                  <Icon name="refresh" size={15} /> Reiniciar equipo
                </button>
                <hr />
                <button
                  type="button"
                  className="menu-item"
                  disabled={viewOnly}
                  onClick={() =>
                    confirmThen('¿Cambiar a solo ver? Ya no podrá controlar el equipo en esta sesión.', () =>
                      send({ tipo: 'solo_ver' }),
                    )
                  }
                >
                  <Icon name="eye" size={15} /> Cambiar a solo ver
                </button>
              </Menu>
              <button
                type="button"
                className="tool-button"
                aria-pressed={pinned}
                title={pinned ? 'Barra fija: clic para que se oculte sola' : 'Se oculta sola: clic para dejarla fija'}
                onClick={togglePinned}
              >
                <Icon name="pin" size={16} />
                <span>{pinned ? 'Fija' : 'Auto'}</span>
              </button>
              <button type="button" className="toolbar-end" onClick={finish}>
                <Icon name="hangUp" size={16} /> Terminar
              </button>
            </div>
          )}

          {(notice ?? secureDesktop) && (
            <div className="desktop-toast" role="status">
              {notice ?? secureDesktop}
            </div>
          )}
        </div>

        {filesOpen && launch && (
          <FilePanel socket={phase.socket} machineName={launch.machineName} onClose={() => setFilesOpen(false)} />
        )}
      </div>

      <footer className="desktop-status">
        <span>
          <span className="status-dot-live" aria-hidden="true" /> Sesión SES-{launch?.sessionId}
        </span>
        <span>
          <Icon name="user" size={13} /> {launch?.technician ?? 'Técnico'} → {ready?.user ?? 'sin usuario conectado'}
        </span>
        <span>
          <Icon name={viewOnly ? 'eye' : 'cursor'} size={13} /> {viewOnly ? 'Solo ver' : 'Control total'}
          {inputBlocked && ' · entrada del usuario bloqueada'}
          {blackScreen && ' · pantalla del usuario en negro'}
        </span>
        <span className="status-spacer" />
        <span className="mono">
          {size} · {fit === 'ajustar' ? 'ajustada' : fit === 'real' ? 'tamaño real' : 'estirada'}
        </span>
        <span className="mono">Calidad {qualityLabel}</span>
        <span className="mono">
          <span className="status-dot-live" aria-hidden="true" /> {stats.latency === null ? '—' : `${stats.latency} ms`}
          {' · '}
          {stats.fps} fps
        </span>
        <span>
          <Icon name="lock" size={13} /> {secure ? 'Conexión cifrada (TLS)' : 'Sin cifrar (solo desarrollo)'}
        </span>
      </footer>
    </div>
  );
}

function ToolButton({
  icon,
  label,
  text,
  onClick,
  disabled,
  pressed,
  title,
}: {
  icon: IconName;
  label: string;
  text?: string;
  onClick: () => void;
  disabled?: boolean;
  pressed?: boolean;
  title?: string;
}) {
  return (
    <button
      type="button"
      className="tool-button"
      aria-label={label}
      title={title ?? label}
      aria-pressed={pressed}
      disabled={disabled}
      onClick={onClick}
    >
      <Icon name={icon} size={16} />
      {text && <span>{text}</span>}
    </button>
  );
}

/** Botón de la barra con su menú desplegable (uno abierto a la vez; se cierra al hacer clic fuera o con Escape). */
function Menu({
  id,
  icon,
  label,
  text,
  open,
  setOpen,
  disabled,
  align,
  children,
}: {
  id: string;
  icon: IconName;
  label: string;
  text?: string;
  open: string | null;
  setOpen: (id: string | null) => void;
  disabled?: boolean;
  align?: 'right';
  children: ReactNode;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const isOpen = open === id;
  useEffect(() => {
    if (!isOpen) return;
    const onClick = (event: MouseEvent) => {
      if (!ref.current?.contains(event.target as Node)) setOpen(null);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(null);
    };
    document.addEventListener('mousedown', onClick);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClick);
      document.removeEventListener('keydown', onKey);
    };
  }, [isOpen, setOpen]);

  return (
    <div className="tool-menu" ref={ref}>
      <button
        type="button"
        className="tool-button"
        aria-label={label}
        title={label}
        aria-haspopup="menu"
        aria-expanded={isOpen}
        disabled={disabled}
        onClick={() => setOpen(isOpen ? null : id)}
      >
        <Icon name={icon} size={16} />
        {text && <span>{text}</span>}
        <Icon name="chevronDown" size={12} />
      </button>
      {isOpen && (
        <div className="tool-popover" data-align={align} role="menu" aria-label={label}>
          {children}
        </div>
      )}
    </div>
  );
}

/** W-29 Conectando: los pasos hasta ver la pantalla. */
function ConnectingSteps({
  machine,
  agentVersion,
  channel,
  permission,
  secure,
  onCancel,
}: {
  machine: string;
  agentVersion?: string;
  channel: boolean;
  permission: { user: string; left: number } | null;
  secure: boolean;
  onCancel: () => void;
}) {
  const steps: { label: string; detail?: string; state: 'listo' | 'activo' | 'pendiente' }[] = [
    { label: 'Agente en línea', detail: agentVersion ? `v${agentVersion}` : undefined, state: 'listo' },
    {
      label: secure ? 'Canal cifrado establecido' : 'Canal establecido',
      detail: 'relay',
      state: channel ? 'listo' : 'activo',
    },
    ...(permission
      ? [
          {
            label: `Esperando permiso de ${permission.user}`,
            detail: `0:${String(permission.left).padStart(2, '0')}`,
            state: 'activo' as const,
          },
        ]
      : []),
    { label: 'Iniciando transmisión', state: channel && !permission ? 'activo' : 'pendiente' },
  ];
  return (
    <section className="connect-steps" aria-live="polite">
      <div className="connect-icons" aria-hidden="true">
        <span>
          <Icon name="laptop" size={22} />
        </span>
        <span className="connect-dots">
          <i />
          <i />
          <i />
          <i />
        </span>
        <span>
          <Icon name="monitor" size={22} />
        </span>
      </div>
      <h1>Conectando con {machine}</h1>
      <p className="muted">
        {permission
          ? `Esperando que ${permission.user} acepte la conexión`
          : channel
            ? 'Preparando la pantalla del equipo…'
            : 'Esperando a que el equipo se conecte…'}
      </p>
      <ol>
        {steps.map((s) => (
          <li key={s.label} data-state={s.state}>
            <span className="step-mark" aria-hidden="true">
              {s.state === 'listo' && <Icon name="check" size={12} />}
            </span>
            <span>{s.label}</span>
            {s.detail && <span className="mono muted">{s.detail}</span>}
          </li>
        ))}
      </ol>
      <button type="button" className="btn btn-outline" onClick={onCancel}>
        Cancelar
      </button>
    </section>
  );
}

/** W-29b Error de conexión: qué pasó, causas probables y el código para soporte. */
function ConnectionError({
  machine,
  code,
  message,
  at,
  lastReportAt,
  causes,
  onClose,
  onRetry,
}: {
  machine: string;
  code: string;
  message: string;
  at: Date;
  lastReportAt?: string;
  causes: { icon: IconName; text: string }[];
  onClose: () => void;
  onRetry?: () => void;
}) {
  const ended = code === 'SESION_TERMINADA' || code === 'ERR_SESION_CERRADA';
  return (
    <section className="connect-error" role="alert">
      <span className="connect-error-icon" aria-hidden="true">
        <Icon name="unplug" size={22} />
      </span>
      <h1>{ended ? `Terminó la sesión con ${machine}` : `No pudimos conectar con ${machine}`}</h1>
      <p className="muted">
        {message}
        {lastReportAt && code === 'ERR_RELAY_TIMEOUT' ? ` Último reporte ${timeAgo(lastReportAt)}.` : ''}
      </p>
      {causes.length > 0 && (
        <ul>
          {causes.map((c) => (
            <li key={c.text}>
              <Icon name={c.icon} size={15} /> {c.text}
            </li>
          ))}
        </ul>
      )}
      <code className="connect-error-code">
        {code} · {at.toLocaleTimeString('es-DO', { hour12: false })}
      </code>
      <div className="connect-error-actions">
        <button type="button" className="link-quiet" onClick={onClose}>
          Cerrar
        </button>
        {onRetry && (
          <button type="button" className="btn btn-primary" onClick={onRetry}>
            <Icon name="refresh" size={15} /> {ended ? 'Volver a conectar' : 'Reintentar'}
          </button>
        )}
      </div>
    </section>
  );
}

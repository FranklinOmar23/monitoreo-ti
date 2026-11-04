import { useEffect, useRef, useState } from 'react';

export interface RemoteMonitor {
  indice: number;
  ancho: number;
  alto: number;
  principal: boolean;
}

/** Lo que va pasando en la sesión (para los pasos de W-29 y la barra del visor). */
export type DesktopEvent =
  | { kind: 'permiso'; user: string; seconds: number }
  | { kind: 'listo'; monitors: RemoteMonitor[]; monitor: number; user: string | null }
  | { kind: 'primera_imagen' }
  | { kind: 'fin'; message: string; code: string }
  | { kind: 'escritorio'; label: string | null }
  | { kind: 'aviso'; message: string }
  | { kind: 'portapapeles'; text: string; automatic: boolean }
  | { kind: 'modo'; viewOnly: boolean }
  | { kind: 'entrada_bloqueada'; active: boolean }
  | { kind: 'pantalla_negra'; active: boolean; message: string | null };

/** Latencia (ida y vuelta, ms), cuadros por segundo y consumo (Mb/s) del último segundo. */
export interface DesktopStats {
  latency: number | null;
  fps: number;
  mbps: number;
  /** Cómo llega la imagen: video H.264 (V2) o mosaicos JPEG (V1); null antes de la primera imagen. */
  codec?: 'h264' | 'jpeg' | null;
}

/** Ajuste de la imagen: a la ventana (sin deformar), tamaño real (con desplazamiento) o estirada (llena la ventana). */
export type FitMode = 'ajustar' | 'real' | 'estirar';

/** Mensaje binario de pantalla (ScreenCapture.Capture en el anfitrión). */
export interface ScreenUpdate {
  width: number;
  height: number;
  rects: { x: number; y: number; w: number; h: number; jpeg: Uint8Array }[];
}

export function parseScreenUpdate(buffer: ArrayBuffer): ScreenUpdate | null {
  const view = new DataView(buffer);
  if (buffer.byteLength < 7 || view.getUint8(0) !== 1) return null;
  const update: ScreenUpdate = { width: view.getUint16(1, true), height: view.getUint16(3, true), rects: [] };
  const count = view.getUint16(5, true);
  let offset = 7;
  for (let i = 0; i < count && offset + 12 <= buffer.byteLength; i++) {
    const length = view.getInt32(offset + 8, true);
    update.rects.push({
      x: view.getUint16(offset, true),
      y: view.getUint16(offset + 2, true),
      w: view.getUint16(offset + 4, true),
      h: view.getUint16(offset + 6, true),
      jpeg: new Uint8Array(buffer, offset + 12, length),
    });
    offset += 12 + length;
  }
  return update;
}

/** Cuadro de la pantalla V2 (VideoScreenCapture en el anfitrión): [3][ancho:2][alto:2][clave:1] y H.264 en Annex B. */
export interface VideoUpdate {
  width: number;
  height: number;
  key: boolean;
  data: Uint8Array;
}

export function parseVideoUpdate(buffer: ArrayBuffer): VideoUpdate | null {
  const view = new DataView(buffer);
  if (buffer.byteLength < 7 || view.getUint8(0) !== 3) return null;
  return {
    width: view.getUint16(1, true),
    height: view.getUint16(3, true),
    key: view.getUint8(5) === 1,
    data: new Uint8Array(buffer, 6),
  };
}

/** Para preguntar si se puede decodificar: perfil base restringido, nivel 4 (1920 × 1080 a 30 cuadros). */
export const H264_PROBE_CODEC = 'avc1.42C028';

/** El codec exacto (avc1.PPCCLL) según el SPS del cuadro clave; null si no trae SPS. */
export function h264Codec(annexB: Uint8Array): string | null {
  for (let i = 0; i + 4 < annexB.length; i++) {
    if (annexB[i] !== 0 || annexB[i + 1] !== 0) continue;
    const start = annexB[i + 2] === 1 ? i + 3 : annexB[i + 2] === 0 && annexB[i + 3] === 1 ? i + 4 : -1;
    if (start < 0 || start + 3 >= annexB.length) continue;
    if ((annexB[start]! & 0x1f) === 7) {
      const hex = (n: number) => n.toString(16).padStart(2, '0').toUpperCase();
      return `avc1.${hex(annexB[start + 1]!)}${hex(annexB[start + 2]!)}${hex(annexB[start + 3]!)}`;
    }
    i = start - 1;
  }
  return null;
}

/** ¿Este visor decodifica H.264 con WebCodecs? (Si no, el equipo manda la pantalla V1.) */
export async function canDecodeH264(): Promise<boolean> {
  if (typeof VideoDecoder === 'undefined') return false;
  try {
    return (
      (await VideoDecoder.isConfigSupported({ codec: H264_PROBE_CODEC, optimizeForLatency: true })).supported === true
    );
  } catch {
    return false;
  }
}

/** Rueda del navegador (deltaY positivo = hacia el usuario) → Windows (120 por paso, positivo = hacia adelante). */
export function wheelDelta(delta: number): number {
  return delta === 0 ? 0 : -Math.sign(delta) * 120;
}

/** En qué escritorio de Windows está el equipo, en palabras del técnico (Default es el normal). */
export function desktopLabel(name: string): string | null {
  if (name === 'Default') return null;
  if (name === 'Winlogon') return 'El equipo está en la pantalla de bloqueo o de inicio de sesión';
  return 'El equipo muestra una ventana de seguridad de Windows (UAC)';
}

/** Mensaje de texto del asistente → evento del visor. */
export function desktopEvent(message: Record<string, unknown>): DesktopEvent | null {
  switch (message['tipo']) {
    case 'esperando_permiso':
      return {
        kind: 'permiso',
        user: String(message['usuario'] ?? 'el usuario'),
        seconds: Number(message['segundos'] ?? 30),
      };
    case 'listo':
      return {
        kind: 'listo',
        monitors: (message['monitores'] as RemoteMonitor[] | undefined) ?? [],
        monitor: Number(message['monitor'] ?? 0),
        user: typeof message['usuario'] === 'string' ? message['usuario'] : null,
      };
    case 'fin':
      return { kind: 'fin', message: String(message['motivo'] ?? 'La sesión terminó.'), code: 'SESION_TERMINADA' };
    case 'error':
      return {
        kind: 'fin',
        message: String(message['mensaje'] ?? 'Error en el equipo.'),
        code: String(message['codigo'] ?? 'ERR_AGENTE'),
      };
    case 'escritorio':
      return { kind: 'escritorio', label: desktopLabel(String(message['nombre'] ?? '')) };
    case 'aviso':
      return { kind: 'aviso', message: String(message['mensaje'] ?? '') };
    case 'portapapeles':
      return { kind: 'portapapeles', text: String(message['texto'] ?? ''), automatic: message['automatico'] === true };
    case 'modo':
      return { kind: 'modo', viewOnly: message['soloVer'] === true };
    case 'entrada_bloqueada':
      return { kind: 'entrada_bloqueada', active: message['activo'] === true };
    case 'pantalla_negra':
      return {
        kind: 'pantalla_negra',
        active: message['activo'] === true,
        message: typeof message['mensaje'] === 'string' ? message['mensaje'] : null,
      };
  }
  if (message['relay'] === 'cerrada')
    return {
      kind: 'fin',
      message: `La sesión terminó: ${String(message['motivo'] ?? '')}.`,
      code: 'ERR_SESION_CERRADA',
    };
  return null;
}

interface DesktopViewProps {
  socket: WebSocket;
  onEvent?: (event: DesktopEvent) => void;
  onStats?: (stats: DesktopStats) => void;
  fit?: FitMode;
  /** Solo ver: no se manda teclado ni mouse. */
  viewOnly?: boolean;
  /** Para la captura de pantalla de la barra. */
  canvasRef?: (canvas: HTMLCanvasElement | null) => void;
}

/**
 * W-28 Escritorio remoto: dibuja en un canvas la pantalla del equipo y confirma cada actualización (el asistente no
 * manda más de dos sin confirmar). V2: cuadros H.264 que decodifica WebCodecs; V1 (equipos sin aceleración de video,
 * o "todos los monitores"): rectángulos JPEG que cambian. Mouse y teclado van al equipo con las coordenadas de su
 * pantalla y la tecla física (KeyboardEvent.code). Mide la latencia (ping) y los cuadros y datos por segundo.
 */
export function DesktopView({
  socket,
  onEvent,
  onStats,
  fit = 'ajustar',
  viewOnly = false,
  canvasRef,
}: DesktopViewProps) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const stage = useRef<HTMLDivElement>(null);
  const callbacks = useRef({ onEvent, onStats, viewOnly, fit });
  const [hasImage, setHasImage] = useState(false);

  useEffect(() => {
    callbacks.current = { onEvent, onStats, viewOnly, fit };
  }, [onEvent, onStats, viewOnly, fit]);

  useEffect(() => {
    canvasRef?.(canvas.current);
    return () => canvasRef?.(null);
  }, [canvasRef]);

  // El tamaño en pantalla según el ajuste (las coordenadas siempre se escalan al tamaño real del equipo).
  const fitCanvas = useRef<() => void>(() => undefined);
  useEffect(() => {
    fitCanvas.current = () => {
      const element = canvas.current;
      const area = stage.current;
      if (!element || !area || !element.width || !element.height) return;
      const mode = callbacks.current.fit;
      if (mode === 'real') {
        element.style.width = `${element.width}px`;
        element.style.height = `${element.height}px`;
      } else if (mode === 'estirar') {
        element.style.width = `${area.clientWidth}px`;
        element.style.height = `${area.clientHeight}px`;
      } else {
        const scale = Math.min(area.clientWidth / element.width, area.clientHeight / element.height, 1);
        element.style.width = `${Math.floor(element.width * scale)}px`;
        element.style.height = `${Math.floor(element.height * scale)}px`;
      }
    };
    fitCanvas.current();
  }, [fit]);

  useEffect(() => {
    const element = canvas.current;
    const context = element?.getContext('2d');
    if (!element || !context) return;
    const send = (message: Record<string, unknown>) => {
      if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
    };
    let drawing = Promise.resolve();
    let disposed = false;
    let first = true;
    let frames = 0;
    let bytes = 0;
    let latency: number | null = null;
    let codec: DesktopStats['codec'] = null;

    const resizing = new ResizeObserver(() => fitCanvas.current());
    if (stage.current) resizing.observe(stage.current);

    // Latencia (ping cada 2 s) y estadísticas cada segundo.
    const pinger = setInterval(() => send({ tipo: 'ping', t: performance.now() }), 2000);
    const stats = setInterval(() => {
      callbacks.current.onStats?.({
        latency,
        fps: frames,
        mbps: Math.round(((bytes * 8) / 1_000_000) * 10) / 10,
        codec,
      });
      frames = 0;
      bytes = 0;
    }, 1000);

    const resize = (width: number, height: number) => {
      if (element.width === width && element.height === height) return;
      element.width = width;
      element.height = height;
      fitCanvas.current();
    };
    const shown = () => {
      frames++;
      if (first) {
        first = false;
        setHasImage(true);
        callbacks.current.onEvent?.({ kind: 'primera_imagen' });
      }
    };

    // V2: cada cuadro se confirma cuando el decodificador lo entrega (y se dibuja). Si un cuadro no sale en un segundo
    // se confirma igual, para que el equipo no se quede esperando.
    let decoder: VideoDecoder | null = null;
    let decoderCodec = '';
    let needKey = true;
    let askedKey = false;
    const pending: number[] = [];
    const ackVideo = () => {
      if (pending.shift() !== undefined) send({ tipo: 'ack' });
    };
    const watchdog = setInterval(() => {
      while (pending.length && performance.now() - pending[0]! > 1000) ackVideo();
    }, 250);
    const resetDecoder = () => {
      if (decoder && decoder.state !== 'closed') decoder.close();
      decoder = null;
      needKey = true;
      while (pending.length) ackVideo();
    };
    const requestKey = () => {
      if (askedKey) return;
      askedKey = true;
      send({ tipo: 'completo' });
    };
    const decodeVideo = (video: VideoUpdate) => {
      if (video.key) {
        // Cuadro clave con otro perfil o nivel: un decodificador nuevo.
        const exact = h264Codec(video.data) ?? H264_PROBE_CODEC;
        if (!decoder || decoder.state === 'closed' || exact !== decoderCodec) {
          resetDecoder();
          decoderCodec = exact;
          decoder = new VideoDecoder({
            output: (frame) => {
              if (!disposed) {
                resize(frame.displayWidth, frame.displayHeight);
                context.drawImage(frame, 0, 0);
                shown();
              }
              frame.close();
              ackVideo();
            },
            error: () => {
              // Un cuadro dañado: se pide uno clave y se vuelve a empezar.
              resetDecoder();
              requestKey();
            },
          });
          decoder.configure({ codec: exact, optimizeForLatency: true });
        }
        needKey = false;
        askedKey = false;
      }
      if (needKey || !decoder || decoder.state !== 'configured') {
        // Hasta el próximo cuadro clave no hay con qué decodificar.
        send({ tipo: 'ack' });
        requestKey();
        return;
      }
      pending.push(performance.now());
      decoder.decode(
        new EncodedVideoChunk({
          type: video.key ? 'key' : 'delta',
          timestamp: Math.round(performance.now() * 1000),
          data: video.data,
        }),
      );
    };

    const draw = async (update: ScreenUpdate) => {
      resize(update.width, update.height);
      for (const rect of update.rects) {
        const bitmap = await createImageBitmap(
          new Blob([rect.jpeg as Uint8Array<ArrayBuffer>], { type: 'image/jpeg' }),
        );
        if (disposed) return;
        context.drawImage(bitmap, rect.x, rect.y);
        bitmap.close();
      }
      shown();
    };

    const onMessage = (event: MessageEvent) => {
      if (event.data instanceof ArrayBuffer) {
        bytes += event.data.byteLength;
        const video = typeof VideoDecoder === 'undefined' ? null : parseVideoUpdate(event.data);
        if (video) {
          codec = 'h264';
          decodeVideo(video);
          return;
        }
        const update = parseScreenUpdate(event.data);
        if (update) codec = 'jpeg';
        if (!update) return;
        // En orden, y se confirma cuando ya está dibujada: así la pantalla nunca se atrasa más de dos actualizaciones.
        drawing = drawing
          .then(() => draw(update))
          .catch(() => undefined)
          .then(() => send({ tipo: 'ack' }));
        return;
      }
      if (typeof event.data !== 'string') return;
      let message: Record<string, unknown>;
      try {
        message = JSON.parse(event.data) as Record<string, unknown>;
      } catch {
        return;
      }
      if (message['tipo'] === 'pong') {
        latency = Math.round(performance.now() - Number(message['t'] ?? 0));
        return;
      }
      const parsed = desktopEvent(message);
      if (parsed) callbacks.current.onEvent?.(parsed);
    };
    socket.addEventListener('message', onMessage);
    // Ya se escucha: el asistente puede empezar (permiso, pantalla). Si el visor decodifica H.264, la pantalla V2.
    if (typeof VideoDecoder === 'undefined') send({ tipo: 'ver' });
    else
      void canDecodeH264().then((video) => {
        if (!disposed) send(video ? { tipo: 'ver', video: ['h264'] } : { tipo: 'ver' });
      });

    const point = (event: MouseEvent) => {
      const box = element.getBoundingClientRect();
      return {
        x: Math.round(((event.clientX - box.left) * element.width) / Math.max(1, box.width)),
        y: Math.round(((event.clientY - box.top) * element.height) / Math.max(1, box.height)),
      };
    };
    const control = () => !callbacks.current.viewOnly;
    let pendingMove: { x: number; y: number } | null = null;
    let frame = 0;
    const onMove = (event: MouseEvent) => {
      if (!control()) return;
      pendingMove = point(event);
      // Como mucho un movimiento por cuadro de pantalla.
      frame ||= requestAnimationFrame(() => {
        frame = 0;
        if (pendingMove) send({ tipo: 'raton', accion: 'mover', ...pendingMove });
        pendingMove = null;
      });
    };
    const onButton = (event: MouseEvent) => {
      event.preventDefault();
      element.focus();
      if (!control()) return;
      send({
        tipo: 'raton',
        accion: event.type === 'mousedown' ? 'abajo' : 'arriba',
        boton: event.button,
        ...point(event),
      });
    };
    const onWheel = (event: WheelEvent) => {
      if (!control()) return;
      event.preventDefault();
      const p = point(event);
      if (event.deltaY) send({ tipo: 'raton', accion: 'rueda', delta: wheelDelta(event.deltaY), ...p });
      if (event.deltaX) send({ tipo: 'raton', accion: 'rueda_h', delta: -wheelDelta(event.deltaX), ...p });
    };
    const pressed = new Set<string>();
    const onKey = (event: KeyboardEvent) => {
      // F11 es la pantalla completa del visor.
      if (event.code === 'F11') return;
      event.preventDefault();
      if (!control()) return;
      const down = event.type === 'keydown';
      if (down) pressed.add(event.code);
      else pressed.delete(event.code);
      send({ tipo: 'tecla', codigo: event.code, abajo: down });
    };
    // Al salir de la ventana se sueltan las teclas apretadas (si no, un Ctrl o un Shift quedaría pegado en el equipo).
    const onBlur = () => {
      for (const code of pressed) send({ tipo: 'tecla', codigo: code, abajo: false });
      pressed.clear();
    };
    const noMenu = (event: Event) => event.preventDefault();

    element.addEventListener('mousemove', onMove);
    element.addEventListener('mousedown', onButton);
    element.addEventListener('mouseup', onButton);
    element.addEventListener('wheel', onWheel, { passive: false });
    element.addEventListener('keydown', onKey);
    element.addEventListener('keyup', onKey);
    element.addEventListener('blur', onBlur);
    element.addEventListener('contextmenu', noMenu);
    element.focus();

    return () => {
      disposed = true;
      clearInterval(pinger);
      clearInterval(stats);
      clearInterval(watchdog);
      if (decoder && decoder.state !== 'closed') decoder.close();
      resizing.disconnect();
      cancelAnimationFrame(frame);
      socket.removeEventListener('message', onMessage);
      element.removeEventListener('mousemove', onMove);
      element.removeEventListener('mousedown', onButton);
      element.removeEventListener('mouseup', onButton);
      element.removeEventListener('wheel', onWheel);
      element.removeEventListener('keydown', onKey);
      element.removeEventListener('keyup', onKey);
      element.removeEventListener('blur', onBlur);
      element.removeEventListener('contextmenu', noMenu);
    };
  }, [socket]);

  return (
    <div ref={stage} className="desktop-stage" data-fit={fit}>
      <canvas
        ref={canvas}
        className="desktop-canvas"
        tabIndex={0}
        aria-label="Pantalla del equipo remoto"
        data-ready={hasImage}
        data-view-only={viewOnly}
      />
    </div>
  );
}

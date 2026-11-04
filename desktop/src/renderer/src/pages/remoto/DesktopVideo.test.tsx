import { act, cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DesktopView, h264Codec, parseVideoUpdate } from './DesktopView';

/** SPS (perfil base restringido, nivel 4), PPS y una porción IDR, como los manda el codificador de Windows. */
const KEY_DATA = [0, 0, 0, 1, 0x67, 0x42, 0xc0, 0x28, 0xab, 0, 0, 0, 1, 0x68, 0xce, 0, 0, 0, 1, 0x65, 0x88, 0x84];
const DELTA_DATA = [0, 0, 0, 1, 0x41, 0x9a, 0x02];

/** Arma un cuadro de la pantalla V2 como lo manda el asistente (VideoScreenCapture.Capture). */
function videoFrame(key: boolean, data: number[], width = 1920, height = 1080) {
  const buffer = new ArrayBuffer(6 + data.length);
  const view = new DataView(buffer);
  view.setUint8(0, 3);
  view.setUint16(1, width, true);
  view.setUint16(3, height, true);
  view.setUint8(5, key ? 1 : 0);
  new Uint8Array(buffer, 6).set(data);
  return buffer;
}

/** VideoDecoder de mentira: cada cuadro sale enseguida, como con optimizeForLatency. */
class FakeDecoder {
  static configured: string[] = [];
  state: 'unconfigured' | 'configured' | 'closed' = 'unconfigured';
  constructor(private readonly init: { output: (frame: unknown) => void; error: (e: unknown) => void }) {}
  static isConfigSupported() {
    return Promise.resolve({ supported: true });
  }
  configure(config: { codec: string }) {
    FakeDecoder.configured.push(config.codec);
    this.state = 'configured';
  }
  decode() {
    queueMicrotask(() => this.init.output({ displayWidth: 1920, displayHeight: 1080, close: () => undefined }));
  }
  close() {
    this.state = 'closed';
  }
}

function fakeSocket() {
  const send = vi.fn<(data: string) => void>();
  const socket = Object.assign(new EventTarget(), { readyState: WebSocket.OPEN, send }) as unknown as WebSocket;
  const sent = () => send.mock.calls.map(([data]) => JSON.parse(data) as Record<string, unknown>);
  const receive = (data: ArrayBuffer) => act(() => void socket.dispatchEvent(new MessageEvent('message', { data })));
  return { socket, sent, receive };
}

const drawImage = vi.fn();

beforeEach(() => {
  FakeDecoder.configured = [];
  drawImage.mockClear();
  vi.stubGlobal('VideoDecoder', FakeDecoder);
  vi.stubGlobal('EncodedVideoChunk', class {});
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      disconnect() {}
    },
  );
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage } as never);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('pantalla V2 (H.264, etapa 7.5)', () => {
  it('lee el cuadro de video y el codec exacto de su SPS', () => {
    const frame = parseVideoUpdate(videoFrame(true, KEY_DATA))!;
    expect(frame).toMatchObject({ width: 1920, height: 1080, key: true });
    expect([...frame.data]).toEqual(KEY_DATA);
    expect(h264Codec(frame.data)).toBe('avc1.42C028');
    expect(h264Codec(new Uint8Array(DELTA_DATA))).toBeNull();
    expect(parseVideoUpdate(new ArrayBuffer(4))).toBeNull();
  });

  it('avisa que decodifica H.264, espera el cuadro clave y dibuja y confirma cada cuadro', async () => {
    const { socket, sent, receive } = fakeSocket();
    const onStats = vi.fn();
    const { container } = render(<DesktopView socket={socket} onStats={onStats} />);

    await waitFor(() => expect(sent()).toContainEqual({ tipo: 'ver', video: ['h264'] }));

    // Un cuadro intermedio sin el clave antes: no se puede decodificar; se confirma y se pide uno clave.
    receive(videoFrame(false, DELTA_DATA));
    expect(sent()).toContainEqual({ tipo: 'ack' });
    expect(sent()).toContainEqual({ tipo: 'completo' });
    expect(drawImage).not.toHaveBeenCalled();

    receive(videoFrame(true, KEY_DATA));
    receive(videoFrame(false, DELTA_DATA));
    await waitFor(() => expect(drawImage).toHaveBeenCalledTimes(2));
    expect(FakeDecoder.configured).toEqual(['avc1.42C028']);
    expect(sent().filter((m) => m['tipo'] === 'ack')).toHaveLength(3);
    const canvas = container.querySelector('canvas')!;
    expect([canvas.width, canvas.height]).toEqual([1920, 1080]);
  });

  it('sin WebCodecs pide la pantalla V1', async () => {
    vi.stubGlobal('VideoDecoder', undefined);
    const { socket, sent } = fakeSocket();
    render(<DesktopView socket={socket} />);
    await waitFor(() => expect(sent()).toContainEqual({ tipo: 'ver' }));
  });
});

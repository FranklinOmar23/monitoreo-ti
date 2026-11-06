import { generateKeyPairSync, sign } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Envelope } from '@monitoreoti/shared';
import {
  BackupScheduler,
  configPayload,
  lastSlot,
  nextSlot,
  runNowPayload,
  type BackupHost,
  type BackupJobConfig,
  type BackupStep,
} from './backups';

const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
const key = publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
const signWith = (text: string, k = privateKey) =>
  sign('sha256', Buffer.from(text), { key: k, dsaEncoding: 'ieee-p1363' }).toString('base64');

const job = (overrides: Partial<BackupJobConfig> = {}): BackupJobConfig => ({
  id: 5,
  nombre: 'ERP a disco externo',
  origen: 'sql',
  instancia_sql: 'MSSQLSERVER',
  elementos: ['ERP_Caribe'],
  modo: 'caliente',
  servicios_detener: [],
  frio_max_minutos: 60,
  estrategia: 'incremental',
  completo_cada_dias: 7,
  destino: { tipo: 'disco_externo', ruta: 'Centinela', discos: [{ serie: 'WD-123', nombre: 'WD Elements' }], expulsar: false, cuenta: null },
  frecuencia: 'diario',
  dias_semana: [],
  hora: '23:00',
  retener_diarios: 7,
  retener_mensuales: 3,
  verificar: true,
  activo: true,
  ...overrides,
});

const signed = (jobs: BackupJobConfig[], machineId = 7) => {
  const config = JSON.stringify({ maquina_id: machineId, trabajos: jobs });
  return { config, firma: signWith(configPayload(machineId, config)) };
};

describe('horarios de los respaldos', () => {
  it('diario: la última hora que ya pasó y la próxima', () => {
    const now = new Date(2026, 10, 6, 10, 0); // viernes 6, 10:00
    expect(lastSlot(job(), now)).toEqual(new Date(2026, 10, 5, 23, 0));
    expect(nextSlot(job(), now)).toEqual(new Date(2026, 10, 6, 23, 0));
  });

  it('semanal: solo los días elegidos (0 = domingo)', () => {
    const weekly = job({ frecuencia: 'semanal', dias_semana: [1] }); // lunes
    const now = new Date(2026, 10, 6, 10, 0); // viernes
    expect(lastSlot(weekly, now)).toEqual(new Date(2026, 10, 2, 23, 0));
    expect(nextSlot(weekly, now)).toEqual(new Date(2026, 10, 9, 23, 0));
  });
});

describe('programador de respaldos (decisión 82)', () => {
  let dir: string;
  let now: Date;
  let sent: Envelope[];
  let host: BackupHost & { requests: unknown[]; steps: BackupStep[] };

  const scheduler = () =>
    new BackupScheduler({
      machineId: 7,
      credential: 'credencial',
      publicKey: () => key,
      dataDir: dir,
      host,
      submit: (e) => sent.push(e),
      log: () => {},
      now: () => now,
    });

  beforeEach(() => {
    dir = mkdtempSync(join(tmpdir(), 'respaldos-'));
    now = new Date(2026, 10, 6, 10, 0);
    sent = [];
    const requests: unknown[] = [];
    host = {
      requests,
      steps: [{ estado: 'correcto', tipo: 'diferencial', bytes_copiados: 10, bytes_totales: 100, fin: true }],
      run: vi.fn(async (request: unknown, onStep: (s: BackupStep) => void) => {
        requests.push(request);
        for (const step of host.steps) onStep(step);
      }),
      explore: vi.fn(async () => ({})),
      disks: vi.fn(async () => ({ ok: true, discos: [{ serie: 'WD-123' }] })),
      test: vi.fn(async () => ({ ok: true })),
    };
  });

  afterEach(() => rmSync(dir, { recursive: true, force: true }));

  const states = () => sent.filter((e) => e.tipo === 'respaldo_estado').map((e) => e.datos as Record<string, unknown>);

  it('ignora una configuración que no firmó la empresa o que es de otro equipo', () => {
    const s = scheduler();
    const other = generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey;
    const config = JSON.stringify({ maquina_id: 7, trabajos: [job()] });
    s.configure({ config, firma: signWith(configPayload(7, config), other) });
    expect(s.jobCount).toBe(0);
    s.configure(signed([job()], 8));
    expect(s.jobCount).toBe(0);
    s.configure(signed([job()]));
    expect(s.jobCount).toBe(1);
  });

  it('un trabajo nuevo no hace "el de ayer": espera a su hora y entonces respalda', async () => {
    const s = scheduler();
    s.configure(signed([job()]));
    s.tick();
    expect(host.run).not.toHaveBeenCalled();

    now = new Date(2026, 10, 6, 23, 0, 30);
    s.tick();
    await vi.waitFor(() => expect(host.run).toHaveBeenCalledTimes(1));
    // Inicio y final, con el mismo id de ejecución.
    await vi.waitFor(() => expect(states().map((x) => x['estado'])).toEqual(['en_curso', 'correcto']));
    expect(new Set(states().map((x) => x['ejecucion'])).size).toBe(1);
    expect(states()[1]).toMatchObject({ trabajoId: 5, tipo: 'diferencial', disparo: 'programado', bytesCopiados: 10 });

    // No se repite en el siguiente tic.
    s.tick();
    expect(host.run).toHaveBeenCalledTimes(1);
  });

  it('la configuración queda guardada: tras reiniciar sigue respaldando sin servidor', async () => {
    scheduler().configure(signed([job()]));
    const restarted = scheduler();
    restarted.start();
    restarted.stop();
    expect(restarted.jobCount).toBe(1);
    now = new Date(2026, 10, 6, 23, 1);
    restarted.tick();
    await vi.waitFor(() => expect(host.run).toHaveBeenCalledTimes(1));
  });

  it('Respaldar ahora solo con firma válida y sin vencer; "completo" fuerza la copia completa', async () => {
    const s = scheduler();
    s.configure(signed([job()]));
    const expires = new Date(now.getTime() + 600_000).toISOString();
    const order = (full: boolean, firma?: string) => ({
      maquina_id: 7,
      trabajo_id: 5,
      ejecucion: 'run-1',
      completo: full,
      vence: expires,
      firma: firma ?? signWith(runNowPayload(7, 5, 'run-1', full, expires)),
    });

    // Firmado como "normal" pero pide "completo": no vale.
    s.runNow({ ...order(false), completo: true });
    expect(host.run).not.toHaveBeenCalled();

    s.runNow(order(true));
    await vi.waitFor(() => expect(host.run).toHaveBeenCalledTimes(1));
    expect(host.requests[0]).toMatchObject({ ejecucion: 'run-1', forzar_completo: true, trabajo: { id: 5 } });
    await vi.waitFor(() => expect(states().at(-1)).toMatchObject({ disparo: 'manual', estado: 'correcto' }));
  });

  it('avisa el disco desconectado solo en la hora antes del respaldo', async () => {
    const s = scheduler();
    host.disks = vi.fn(async () => ({ ok: true, discos: [] }));
    s.configure(signed([job()]));
    await vi.waitFor(() => expect(sent.some((e) => e.tipo === 'respaldos_agenda')).toBe(true));
    const agenda = () => (sent.filter((e) => e.tipo === 'respaldos_agenda').at(-1)!.datos as { trabajos: unknown[] }).trabajos[0];
    expect(agenda()).toMatchObject({ trabajoId: 5, discoConectado: null });

    now = new Date(2026, 10, 6, 22, 15);
    await s.reportAgenda();
    expect(agenda()).toMatchObject({ discoConectado: false, proxima: new Date(2026, 10, 6, 23, 0).toISOString() });

    host.disks = vi.fn(async () => ({ ok: true, discos: [{ serie: 'wd-123 ' }] }));
    await s.reportAgenda();
    expect(agenda()).toMatchObject({ discoConectado: true });
  });

  it('la clave de la carpeta de red no va en la configuración guardada en claro', () => {
    const s = scheduler();
    s.configure(signed([job({ destino: { tipo: 'red', ruta: '\\\\nas\\r', discos: [], expulsar: false, cuenta: 'SELLADO' } })]));
    expect(s.jobCount).toBe(1);
    // Si no se puede abrir lo sellado, el respaldo falla con un mensaje claro (y no llama al anfitrión).
    now = new Date(2026, 10, 6, 23, 0, 10);
    s.tick();
    return vi.waitFor(() => {
      expect(host.run).not.toHaveBeenCalled();
      expect(states().at(-1)).toMatchObject({ estado: 'error', detalle: 'No se pudo abrir la cuenta de la carpeta de red.' });
    });
  });
});

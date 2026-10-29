import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { EventsCollector } from './events';
import {
  decodeProductState,
  eventsScript,
  parseDiskHealth,
  parseEvents,
  parseProcesses,
  parseSecurity,
  parseServices,
  parseVolumes,
} from './health';

const GB = 1024 ** 3;

describe('volúmenes', () => {
  it('convierte bytes a GB y descarta unidades sin tamaño', () => {
    const { volumenes } = parseVolumes(
      JSON.stringify([
        { DeviceID: 'C:', VolumeName: 'Windows', Size: 476 * GB, FreeSpace: 39 * GB },
        { DeviceID: 'D:', VolumeName: null, Size: 0, FreeSpace: 0 },
      ]),
    );
    expect(volumenes).toEqual([{ unidad: 'C:', etiqueta: 'Windows', total_gb: 476, libre_gb: 39 }]);
  });
});

describe('procesos', () => {
  it('reparte la CPU entre los núcleos, quita el sufijo #n y ordena por consumo', () => {
    const datos = parseProcesses(
      JSON.stringify({
        nucleos: 4,
        procesos: [
          { Name: 'chrome#2', IDProcess: 4120, PercentProcessorTime: 40, WorkingSetPrivate: 2.4 * GB },
          { Name: 'MSACCESS', IDProcess: 8812, PercentProcessorTime: 56.8, WorkingSetPrivate: 1.82 * GB },
          { Name: 'System', IDProcess: 0, PercentProcessorTime: 1, WorkingSetPrivate: 0 },
        ],
      }),
    );

    expect(datos.total).toBe(2);
    expect(datos.procesos[0]).toEqual({ nombre: 'MSACCESS', pid: 8812, cpu: 14.2, ram_mb: 1863.7 });
    expect(datos.procesos[1]!.nombre).toBe('chrome');
  });

  it('se queda con los 20 que más consumen', () => {
    const procesos = Array.from({ length: 30 }, (_, i) => ({
      Name: `p${i}`,
      IDProcess: i + 1,
      PercentProcessorTime: i,
      WorkingSetPrivate: 0,
    }));
    const datos = parseProcesses(JSON.stringify({ nucleos: 1, procesos }));
    expect(datos.total).toBe(30);
    expect(datos.procesos).toHaveLength(20);
    expect(datos.procesos[0]!.nombre).toBe('p29');
  });
});

describe('servicios', () => {
  it('traduce estado e inicio (incluido el inicio retrasado)', () => {
    const { servicios } = parseServices(
      JSON.stringify([
        {
          Name: 'Spooler',
          DisplayName: 'Cola de impresión',
          State: 'Running',
          StartMode: 'Auto',
          DelayedAutoStart: false,
        },
        { Name: 'gupdate', DisplayName: 'Google Update', State: 'Stopped', StartMode: 'Auto', DelayedAutoStart: true },
      ]),
    );
    expect(servicios).toEqual([
      { nombre: 'Spooler', nombre_visible: 'Cola de impresión', estado: 'en_ejecucion', inicio: 'automatico' },
      { nombre: 'gupdate', nombre_visible: 'Google Update', estado: 'detenido', inicio: 'automatico_retrasado' },
    ]);
  });
});

describe('eventos', () => {
  const raw = (registro: string, id: number, nivel = 2) => ({
    registro,
    id_registro: id,
    fecha: `2026-10-27T08:${String(id % 60).padStart(2, '0')}:00.0000000Z`,
    origen: 'Microsoft-Windows-Kernel-Power',
    codigo: 41,
    nivel,
    mensaje: 'The system has rebooted without cleanly shutting down first.',
  });

  it('solo pasa los eventos nuevos de cada registro y avanza el marcador', () => {
    const now = new Date('2026-10-27T09:00:00Z');
    const { datos, bookmark } = parseEvents(
      JSON.stringify([raw('System', 10, 1), raw('System', 11), raw('Application', 5)]),
      { System: 10, desde: '2026-10-27T08:00:00Z' },
      now,
    );

    expect(datos.eventos.map((e) => `${e.registro}:${e.id_registro}:${e.nivel}`)).toEqual([
      'Application:5:error',
      'System:11:error',
    ]);
    expect(bookmark).toEqual({ System: 11, Application: 5, desde: now.toISOString() });
  });

  it('el script solo acepta fechas ISO armadas por el agente', () => {
    expect(eventsScript('2026-10-27T08:00:00.000Z')).toContain("Parse('2026-10-27T08:00:00.000Z'");
    expect(() => eventsScript("2026'); Remove-Item C:\\ -Recurse; ('")).toThrow();
  });

  describe('EventsCollector', () => {
    let dir: string;
    beforeEach(() => {
      dir = mkdtempSync(join(tmpdir(), 'eventos-'));
    });
    afterEach(() => rmSync(dir, { recursive: true, force: true }));

    it('recuerda lo enviado entre reinicios del servicio', async () => {
      const output = JSON.stringify([raw('System', 1), raw('System', 2)]);
      const first = new EventsCollector(dir, () => Promise.resolve(output));
      expect((await first.collect()).eventos).toHaveLength(2);

      // Un servicio reiniciado vuelve a leer la misma ventana: no repite.
      const second = new EventsCollector(dir, () => Promise.resolve(output));
      expect((await second.collect()).eventos).toHaveLength(0);
    });

    it('la primera vez revisa las últimas 24 horas', async () => {
      let script = '';
      const now = new Date('2026-10-27T12:00:00Z');
      const collector = new EventsCollector(
        dir,
        (s) => ((script = s), Promise.resolve('')),
        () => now,
      );

      await collector.collect();

      expect(script).toContain("Parse('2026-10-26T11:59:00.000Z'");
    });
  });
});

describe('salud de discos', () => {
  it('usa la salud de Windows y la endurece con los contadores SMART', () => {
    const { discos } = parseDiskHealth(
      JSON.stringify([
        {
          modelo: 'Samsung SSD 860',
          tamano: 512 * GB,
          salud: 'Healthy',
          reasignados: 148,
          pendientes: 0,
          desgaste: 12,
        },
        { modelo: 'WD_BLACK SN850X', tamano: 1000 * GB, salud: 'Healthy', desgaste: 3, temperatura: 41 },
        { modelo: 'Viejo', salud: 'Healthy', falla_prevista: true },
        { modelo: 'Malo', salud: 'Unhealthy' },
      ]),
    );

    expect(discos.map((d) => d.salud)).toEqual(['advertencia', 'sano', 'critico', 'critico']);
    expect(discos[0]).toMatchObject({ sectores_reasignados: 148, tamano_gb: 512, desgaste: 12 });
    expect(discos[1]!.temperatura).toBe(41);
  });
});

describe('seguridad', () => {
  it('decodifica productState del Centro de seguridad', () => {
    expect(decodeProductState(0x061100)).toEqual({ activo: true, actualizado: true }); // Defender activo y al día
    expect(decodeProductState(0x060100)).toEqual({ activo: false, actualizado: true }); // desactivado
    expect(decodeProductState(0x061110)).toEqual({ activo: true, actualizado: false }); // firmas viejas
  });

  it('lista antivirus y firewall; sin Centro de seguridad (servidores) no inventa datos', () => {
    expect(
      parseSecurity(
        JSON.stringify({ antivirus: { displayName: 'Windows Defender', productState: 0x061100 }, firewall: true }),
      ),
    ).toEqual({ antivirus: [{ nombre: 'Windows Defender', activo: true, actualizado: true }], firewall_activo: true });
    expect(parseSecurity(JSON.stringify({ antivirus: [], firewall: null }))).toEqual({
      antivirus: [],
      firewall_activo: undefined,
    });
  });
});

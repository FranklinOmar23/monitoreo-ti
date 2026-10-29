import { describe, expect, it } from 'vitest';
import { cpuPercent, ramPercent, usedPercent } from './sampler';
import {
  asArray,
  parseDrives,
  parseHardware,
  parseInterfaces,
  parseLinkSpeed,
  parseSoftware,
  parseTemperature,
} from './windows';

describe('asArray', () => {
  it('normaliza lo que devuelve PowerShell: nada, un objeto o una lista', () => {
    expect(asArray('')).toEqual([]);
    expect(asArray('  \r\n')).toEqual([]);
    expect(asArray('{"a":1}')).toEqual([{ a: 1 }]);
    expect(asArray('[{"a":1},{"a":2}]')).toHaveLength(2);
    expect(asArray('"C:"')).toEqual(['C:']);
  });
});

describe('parseHardware', () => {
  const laptop = JSON.stringify({
    fabricante: 'Lenovo ',
    modelo: 'ThinkPad E14',
    serie: 'PF3ABC12',
    pcSystemType: 2,
    os: { caption: 'Microsoft Windows 11 Pro', version: '10.0.22631' },
    cpu: [{ nombre: 'Intel(R) Core(TM) i5-1235U', nucleos: 10, hilos: 12 }],
    ram: [
      { bytes: 8589934592, mhz: 3200, tipo: 26 },
      { bytes: 8589934592, mhz: 3200, tipo: 26 },
    ],
    discos: [{ modelo: 'Samsung SSD 970', bytes: 512110190592 }],
    gpu: [{ nombre: 'Intel(R) Iris(R) Xe Graphics', bytes: 1073741824 }],
    placa: { fabricante: 'LENOVO', producto: '21CC' },
    bios: { fabricante: 'LENOVO', version: 'N3AET60W' },
    bateria: [{ nombre: 'Internal Battery' }],
  });

  it('arma el inventario de una laptop con sus componentes', () => {
    const hardware = parseHardware(laptop);

    expect(hardware).toMatchObject({
      fabricante: 'Lenovo',
      modelo: 'ThinkPad E14',
      serie: 'PF3ABC12',
      tipo: 'laptop',
      sistema_operativo: 'Microsoft Windows 11 Pro 10.0.22631',
    });
    expect(hardware.componentes.map((c) => c.tipo)).toEqual([
      'cpu',
      'ram',
      'disco',
      'gpu',
      'placa_base',
      'bios',
      'bateria',
    ]);
    expect(hardware.componentes.find((c) => c.tipo === 'ram')).toMatchObject({
      descripcion: '2 × 8 GB DDR4 3200 MHz',
      capacidad: '16 GB',
    });
    expect(hardware.componentes.find((c) => c.tipo === 'disco')?.capacidad).toBe('477 GB');
    expect(hardware.componentes.every((c) => c.estado_salud === 'desconocido')).toBe(true);
  });

  it('describe módulos de RAM distintos sin inventar una configuración simétrica', () => {
    const mixed = JSON.stringify({
      ram: [
        { bytes: 17179869184, mhz: 2667, tipo: 26 },
        { bytes: 8589934592, mhz: 2667, tipo: 26 },
      ],
    });

    expect(parseHardware(mixed).componentes[0]).toMatchObject({
      tipo: 'ram',
      descripcion: '16 GB + 8 GB DDR4 2667 MHz',
      capacidad: '24 GB',
    });
  });

  it('distingue servidor, laptop y computadora de escritorio', () => {
    const desktop = { pcSystemType: 1, os: { caption: 'Microsoft Windows 11 Pro' } };
    expect(parseHardware(JSON.stringify(desktop)).tipo).toBe('computadora');
    expect(
      parseHardware(JSON.stringify({ ...desktop, os: { caption: 'Microsoft Windows Server 2022 Standard' } })).tipo,
    ).toBe('servidor');
    expect(parseHardware(JSON.stringify({ ...desktop, bateria: [{ nombre: 'UPS' }] })).tipo).toBe('laptop');
  });

  it('tolera datos faltantes sin inventar componentes', () => {
    expect(parseHardware('{}').componentes).toEqual([]);
  });
});

describe('parseSoftware', () => {
  it('quita duplicados de las vistas de 64 y 32 bits y convierte la fecha', () => {
    const { programas } = parseSoftware(
      JSON.stringify([
        { nombre: '7-Zip 24.08 (x64)', version: '24.08', editor: 'Igor Pavlov', fecha: '20240910' },
        { nombre: '7-Zip 24.08 (x64)', version: '24.08', editor: 'Igor Pavlov', fecha: '20240910' },
        { nombre: '  ', version: '1' },
        { nombre: 'Chrome', version: '130', editor: '', fecha: 'no-es-fecha' },
        { nombre: 'Fecha imposible', fecha: '20241345' },
      ]),
    );

    expect(programas).toEqual([
      { nombre: '7-Zip 24.08 (x64)', version: '24.08', editor: 'Igor Pavlov', fecha_instalacion: '2024-09-10' },
      { nombre: 'Chrome', version: '130', editor: undefined, fecha_instalacion: undefined },
      { nombre: 'Fecha imposible', version: undefined, editor: undefined, fecha_instalacion: undefined },
    ]);
  });

  it('acepta un solo programa o ninguno', () => {
    expect(parseSoftware('{"nombre":"Solo"}').programas).toHaveLength(1);
    expect(parseSoftware('').programas).toEqual([]);
  });
});

describe('parseInterfaces', () => {
  const adapters = JSON.stringify([
    {
      Name: 'Ethernet',
      MacAddress: '3C-52-82-A1-00-03',
      InterfaceDescription: 'Intel I219-V',
      LinkSpeed: '1 Gbps',
      Virtual: false,
    },
    {
      Name: 'Wi-Fi',
      MacAddress: '3c-52-82-a1-f0-03',
      InterfaceDescription: 'Intel Wi-Fi 6 AX201',
      LinkSpeed: '866.7 Mbps',
      Virtual: false,
    },
    {
      Name: 'vEthernet (WSL)',
      MacAddress: '00-15-5D-00-00-01',
      InterfaceDescription: 'Hyper-V Virtual Ethernet Adapter',
      LinkSpeed: '10 Gbps',
      Virtual: true,
    },
    { Name: 'Bluetooth', MacAddress: '', InterfaceDescription: 'Bluetooth Device', LinkSpeed: '3 Mbps' },
    { Name: 'Duplicado', MacAddress: '3C-52-82-A1-00-03', InterfaceDescription: 'Intel I219-V', LinkSpeed: '1 Gbps' },
  ]);

  it('clasifica los adaptadores, normaliza la MAC y descarta los que no tienen', () => {
    const { interfaces } = parseInterfaces(adapters, new Map([['Ethernet', '10.10.20.31']]));

    expect(interfaces).toEqual([
      { nombre: 'Ethernet', mac: '3C:52:82:A1:00:03', ip: '10.10.20.31', tipo: 'cableada', velocidad_mbps: 1000 },
      { nombre: 'Wi-Fi', mac: '3C:52:82:A1:F0:03', ip: undefined, tipo: 'wifi', velocidad_mbps: 867 },
      { nombre: 'vEthernet (WSL)', mac: '00:15:5D:00:00:01', ip: undefined, tipo: 'virtual', velocidad_mbps: 10000 },
    ]);
  });

  it('convierte velocidades de enlace', () => {
    expect(parseLinkSpeed('100 Mbps')).toBe(100);
    expect(parseLinkSpeed('2.5 Gbps')).toBe(2500);
    expect(parseLinkSpeed('0 bps')).toBeUndefined();
    expect(parseLinkSpeed('desconocido')).toBeUndefined();
    expect(parseLinkSpeed(null)).toBeUndefined();
  });
});

describe('temperatura y discos', () => {
  it('convierte décimas de kelvin a °C y toma la zona más caliente', () => {
    expect(parseTemperature('[3182,3332]')).toBe(60.05);
    expect(parseTemperature('3002')).toBe(27.05);
  });

  it('descarta lecturas imposibles y la ausencia de sensor', () => {
    expect(parseTemperature('')).toBeNull();
    expect(parseTemperature('[0]')).toBeNull();
    expect(parseTemperature('[9999]')).toBeNull();
  });

  it('solo acepta letras de unidad', () => {
    expect(parseDrives('["C:","D:","basura"]')).toEqual(['C:', 'D:']);
    expect(parseDrives('"C:"')).toEqual(['C:']);
  });
});

describe('cálculo de porcentajes', () => {
  it('CPU entre dos lecturas', () => {
    expect(cpuPercent({ idle: 100, total: 200 }, { idle: 150, total: 400 })).toBe(75);
    expect(cpuPercent({ idle: 100, total: 200 }, { idle: 100, total: 200 })).toBeNull();
  });

  it('RAM y disco', () => {
    expect(ramPercent(16, 4)).toBe(75);
    expect(ramPercent(0, 0)).toBeNull();
    expect(usedPercent(1000, 80)).toBe(92);
    expect(usedPercent(0, 0)).toBeNull();
  });
});

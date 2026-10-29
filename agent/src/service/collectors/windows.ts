import type {
  ComponenteDatos,
  InterfazRedDatos,
  InterfacesRedDatos,
  InventarioHardwareDatos,
  InventarioSoftwareDatos,
  ProgramaDatos,
  TipoInterfaz,
} from '@monitoreoti/shared';
import { runPowerShell } from '../powershell';

// Scripts de PowerShell 5.1 (WMI/CIM y registro) y sus parsers. Los parsers son funciones puras para
// probarlos sin Windows. No se usa Win32_Product: dispara una reconfiguración de cada MSI instalado.

const PREAMBLE = `$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n`;

export const HARDWARE_SCRIPT = `${PREAMBLE}
$cs = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
$bb = Get-CimInstance Win32_BaseBoard
$os = Get-CimInstance Win32_OperatingSystem
[pscustomobject]@{
  fabricante = $cs.Manufacturer; modelo = $cs.Model; serie = $bios.SerialNumber; pcSystemType = $cs.PCSystemType
  os = @{ caption = $os.Caption; version = $os.Version; build = $os.BuildNumber }
  cpu = @(Get-CimInstance Win32_Processor | ForEach-Object { @{ nombre = $_.Name; nucleos = $_.NumberOfCores; hilos = $_.NumberOfLogicalProcessors } })
  ram = @(Get-CimInstance Win32_PhysicalMemory | ForEach-Object { @{ bytes = $_.Capacity; mhz = $_.Speed; tipo = $_.SMBIOSMemoryType } })
  discos = @(Get-CimInstance Win32_DiskDrive | ForEach-Object { @{ modelo = $_.Model; bytes = $_.Size } })
  gpu = @(Get-CimInstance Win32_VideoController | ForEach-Object { @{ nombre = $_.Name; bytes = $_.AdapterRAM } })
  placa = @{ fabricante = $bb.Manufacturer; producto = $bb.Product }
  bios = @{ version = $bios.SMBIOSBIOSVersion; fabricante = $bios.Manufacturer }
  bateria = @(Get-CimInstance Win32_Battery | ForEach-Object { @{ nombre = $_.Name } })
} | ConvertTo-Json -Depth 5 -Compress
`;

export const SOFTWARE_SCRIPT = `${PREAMBLE}
$paths = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*', 'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'
Get-ItemProperty $paths -ErrorAction SilentlyContinue |
  Where-Object { $_.DisplayName -and -not $_.SystemComponent -and -not $_.ParentKeyName } |
  ForEach-Object { [pscustomobject]@{ nombre = $_.DisplayName; version = $_.DisplayVersion; editor = $_.Publisher; fecha = $_.InstallDate } } |
  ConvertTo-Json -Compress
`;

export const NETWORK_SCRIPT = `${PREAMBLE}
Get-NetAdapter | Select-Object Name, MacAddress, InterfaceDescription, LinkSpeed, Virtual |
  ConvertTo-Json -Compress
`;

export const DRIVES_SCRIPT = `${PREAMBLE}
Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | Select-Object -ExpandProperty DeviceID | ConvertTo-Json -Compress
`;

/** Zonas térmicas ACPI (décimas de kelvin). Muchos equipos no las exponen: en ese caso no hay temperatura. */
export const TEMPERATURE_SCRIPT = `${PREAMBLE}
try {
  Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature |
    Select-Object -ExpandProperty CurrentTemperature | ConvertTo-Json -Compress
} catch {
  # Sin sensor o sin permiso (fuera de LocalSystem): no hay dato, no es un error.
}
`;

/** PowerShell devuelve un objeto suelto cuando hay uno solo y nada cuando no hay ninguno. */
export function asArray<T>(json: string): T[] {
  const text = json.trim();
  if (!text) return [];
  const parsed: unknown = JSON.parse(text);
  if (parsed === null || parsed === undefined) return [];
  return Array.isArray(parsed) ? (parsed as T[]) : [parsed as T];
}

const GIB = 1024 ** 3;

function gigabytes(bytes: number | null | undefined): string | undefined {
  if (!bytes || bytes <= 0) return undefined;
  const gb = bytes / GIB;
  return `${gb >= 10 ? Math.round(gb) : Math.round(gb * 10) / 10} GB`;
}

const MEMORY_TYPES: Record<number, string> = { 20: 'DDR', 21: 'DDR2', 24: 'DDR3', 26: 'DDR4', 34: 'DDR5' };

interface HardwareRaw {
  fabricante?: string | null;
  modelo?: string | null;
  serie?: string | null;
  pcSystemType?: number | null;
  os?: { caption?: string | null; version?: string | null } | null;
  cpu?: { nombre?: string | null; nucleos?: number | null; hilos?: number | null }[];
  ram?: { bytes?: number | null; mhz?: number | null; tipo?: number | null }[];
  discos?: { modelo?: string | null; bytes?: number | null }[];
  gpu?: { nombre?: string | null; bytes?: number | null }[];
  placa?: { fabricante?: string | null; producto?: string | null } | null;
  bios?: { version?: string | null; fabricante?: string | null } | null;
  bateria?: { nombre?: string | null }[];
}

const clean = (value: string | null | undefined): string | undefined => {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
};

export function parseHardware(json: string): InventarioHardwareDatos {
  const raw = JSON.parse(json) as HardwareRaw;
  const componentes: ComponenteDatos[] = [];
  // SMART y estado de componentes llegan en la Fase 3: hasta entonces la salud es desconocida.
  const add = (tipo: string, descripcion: string | undefined, capacidad?: string) => {
    if (descripcion) componentes.push({ tipo, descripcion, capacidad, estado_salud: 'desconocido' });
  };

  for (const cpu of raw.cpu ?? []) {
    add('cpu', clean(cpu.nombre), cpu.nucleos ? `${cpu.nucleos} núcleos` : undefined);
  }

  const modules = raw.ram ?? [];
  if (modules.length > 0) {
    const total = modules.reduce((sum, m) => sum + (m.bytes ?? 0), 0);
    const type = MEMORY_TYPES[modules[0]?.tipo ?? 0];
    const speed = modules[0]?.mhz ? ` ${modules[0].mhz} MHz` : '';
    // Módulos iguales: "2 × 8 GB"; distintos: "16 GB + 8 GB".
    const sizes = modules.map((m) => gigabytes(m.bytes) ?? '?');
    const layout = sizes.every((size) => size === sizes[0]) ? `${sizes.length} × ${sizes[0]}` : sizes.join(' + ');
    add('ram', `${layout}${type ? ` ${type}` : ''}${speed}`, gigabytes(total));
  }

  for (const disk of raw.discos ?? []) add('disco', clean(disk.modelo), gigabytes(disk.bytes));
  for (const gpu of raw.gpu ?? []) add('gpu', clean(gpu.nombre), gigabytes(gpu.bytes));

  const board = [clean(raw.placa?.fabricante), clean(raw.placa?.producto)].filter(Boolean).join(' ');
  add('placa_base', board || undefined);
  add('bios', [clean(raw.bios?.fabricante), clean(raw.bios?.version)].filter(Boolean).join(' ') || undefined);
  for (const battery of raw.bateria ?? []) add('bateria', clean(battery.nombre) ?? 'Batería');

  const caption = clean(raw.os?.caption);
  const isLaptop = raw.pcSystemType === 2 || (raw.bateria?.length ?? 0) > 0;
  const isServer = caption?.toLowerCase().includes('server') ?? false;

  return {
    fabricante: clean(raw.fabricante),
    modelo: clean(raw.modelo),
    serie: clean(raw.serie),
    tipo: isServer ? 'servidor' : isLaptop ? 'laptop' : 'computadora',
    sistema_operativo: [caption, clean(raw.os?.version)].filter(Boolean).join(' ') || undefined,
    ram_total_mb:
      modules.length > 0 ? Math.round(modules.reduce((sum, m) => sum + (m.bytes ?? 0), 0) / 1024 / 1024) : undefined,
    componentes,
  };
}

interface SoftwareRaw {
  nombre?: string | null;
  version?: string | null;
  editor?: string | null;
  fecha?: string | null;
}

export const MAX_PROGRAMS = 5000;

export function parseSoftware(json: string): InventarioSoftwareDatos {
  const seen = new Set<string>();
  const programas: ProgramaDatos[] = [];

  for (const item of asArray<SoftwareRaw>(json)) {
    const nombre = clean(item.nombre);
    if (!nombre) continue;

    // El mismo programa aparece en la vista de 64 y de 32 bits del registro.
    const key = `${nombre}|${item.version ?? ''}`.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);

    programas.push({
      nombre,
      version: clean(item.version),
      editor: clean(item.editor),
      fecha_instalacion: parseInstallDate(item.fecha),
    });
    if (programas.length >= MAX_PROGRAMS) break;
  }

  return { programas };
}

/** El registro guarda InstallDate como AAAAMMDD. */
function parseInstallDate(value: string | null | undefined): string | undefined {
  const match = /^(\d{4})(\d{2})(\d{2})$/.exec(value?.trim() ?? '');
  if (!match) return undefined;
  const [, year, month, day] = match;
  const date = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day)));
  const valid = date.getUTCFullYear() === Number(year) && date.getUTCMonth() === Number(month) - 1;
  return valid && Number(year) >= 1990 ? `${year}-${month}-${day}` : undefined;
}

interface AdapterRaw {
  Name?: string | null;
  MacAddress?: string | null;
  InterfaceDescription?: string | null;
  LinkSpeed?: string | null;
  Virtual?: boolean | null;
}

const WIFI_PATTERN = /wi-?fi|wireless|802\.11|wlan/i;
const VIRTUAL_PATTERN = /virtual|vpn|tap-|vmware|hyper-v|loopback|bluetooth|miniport|wan |wsl/i;

export function parseInterfaces(json: string, ipByName: ReadonlyMap<string, string> = new Map()): InterfacesRedDatos {
  const seen = new Set<string>();
  const interfaces: InterfazRedDatos[] = [];

  for (const adapter of asArray<AdapterRaw>(json)) {
    const mac = normalizeMac(adapter.MacAddress);
    const nombre = clean(adapter.Name);
    if (!mac || !nombre || seen.has(mac)) continue;
    seen.add(mac);

    const description = adapter.InterfaceDescription ?? '';
    const tipo: TipoInterfaz =
      adapter.Virtual === true || VIRTUAL_PATTERN.test(`${nombre} ${description}`)
        ? 'virtual'
        : WIFI_PATTERN.test(`${nombre} ${description}`)
          ? 'wifi'
          : 'cableada';

    interfaces.push({
      nombre,
      mac,
      ip: ipByName.get(nombre),
      tipo,
      velocidad_mbps: parseLinkSpeed(adapter.LinkSpeed),
    });
  }

  return { interfaces };
}

function normalizeMac(value: string | null | undefined): string | undefined {
  const mac = value?.trim().toUpperCase().replaceAll('-', ':');
  return mac && /^([0-9A-F]{2}:){5}[0-9A-F]{2}$/.test(mac) && mac !== '00:00:00:00:00:00' ? mac : undefined;
}

/** "1 Gbps", "866.7 Mbps", "100 Mbps" → megabits por segundo. */
export function parseLinkSpeed(value: string | null | undefined): number | undefined {
  const match = /^([\d.,]+)\s*(g|m|k)?bps$/i.exec(value?.trim() ?? '');
  if (!match) return undefined;

  const amount = Number((match[1] ?? '').replace(',', '.'));
  const unit = (match[2] ?? '').toLowerCase();
  const mbps = unit === 'g' ? amount * 1000 : unit === 'k' ? amount / 1000 : amount;
  return Number.isFinite(mbps) && mbps > 0 ? Math.round(mbps) : undefined;
}

/** Décimas de kelvin → °C; descarta lecturas imposibles y devuelve la zona más caliente. */
export function parseTemperature(json: string): number | null {
  const celsius = asArray<number>(json)
    .map((tenthsKelvin) => tenthsKelvin / 10 - 273.15)
    .filter((c) => c > 0 && c < 150);
  return celsius.length > 0 ? Math.round(Math.max(...celsius) * 100) / 100 : null;
}

export function parseDrives(json: string): string[] {
  return asArray<string>(json).filter((id) => /^[A-Za-z]:$/.test(id));
}

export const collectHardware = async () => parseHardware(await runPowerShell(HARDWARE_SCRIPT, { timeoutMs: 120_000 }));
export const collectSoftware = async () => parseSoftware(await runPowerShell(SOFTWARE_SCRIPT, { timeoutMs: 120_000 }));
export const collectInterfaces = async (ipByName: ReadonlyMap<string, string>) =>
  parseInterfaces(await runPowerShell(NETWORK_SCRIPT), ipByName);
export const collectDrives = async () => parseDrives(await runPowerShell(DRIVES_SCRIPT));
export const collectTemperature = async () => parseTemperature(await runPowerShell(TEMPERATURE_SCRIPT));

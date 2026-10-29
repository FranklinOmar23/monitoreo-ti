import { hostname, networkInterfaces, release, type } from 'node:os';
import { runPowerShell } from './powershell';

export interface MachineIdentity {
  hostname: string;
  /** MAC principal en formato AA:BB:CC:DD:EE:FF. */
  macAddress: string;
  ipAddress: string | undefined;
  operatingSystem: string;
}

type Interfaces = ReturnType<typeof networkInterfaces>;

/** Adaptadores que no identifican al equipo físico: virtuales, VPN, Bluetooth, loopback. */
const VIRTUAL_NAME = /vethernet|virtualbox|vmware|hyper-v|loopback|bluetooth|tap-|vpn|wsl|docker|vbox|hamachi/i;

/** Prefijos de fabricante de tarjetas virtuales (Hyper-V, VirtualBox, VMware, Parallels). */
const VIRTUAL_OUI = ['00:15:5D', '08:00:27', '00:05:69', '00:0C:29', '00:1C:14', '00:50:56', '00:1C:42'];

/**
 * MAC que no es de una tarjeta física: ceros, prefijos de máquinas virtuales o "administrada localmente"
 * (bit 1 del primer byte). VirtualBox host-only, Hamachi o las VPN usan este último aunque el adaptador
 * se llame "Ethernet 3".
 */
export function isVirtualMac(mac: string): boolean {
  const upper = mac.toUpperCase();
  if (upper === '00:00:00:00:00:00') return true;
  if (VIRTUAL_OUI.some((oui) => upper.startsWith(oui))) return true;
  const firstByte = Number.parseInt(upper.slice(0, 2), 16);
  return Number.isNaN(firstByte) || (firstByte & 0x02) !== 0;
}

/**
 * Elige el adaptador principal. Si Windows indicó cuál es (el físico de la ruta por defecto), se usa ese;
 * si no, el primero con IPv4 que no sea interno ni virtual.
 */
export function pickPrimaryInterface(
  interfaces: Interfaces,
  preferredName?: string,
): { mac: string; ip: string | undefined } | undefined {
  const candidates = Object.entries(interfaces).flatMap(([name, addresses]) =>
    (addresses ?? [])
      .filter((a) => !a.internal && !isVirtualMac(a.mac) && !VIRTUAL_NAME.test(name))
      .map((a) => ({ ...a, name })),
  );

  const usable = (a: (typeof candidates)[number]) => a.family === 'IPv4' && !a.address.startsWith('169.254.');
  const preferred = preferredName ? candidates.filter((a) => a.name === preferredName) : [];
  const pool = preferred.length > 0 ? preferred : candidates;

  const withIpv4 = pool.find(usable);
  const chosen = withIpv4 ?? pool[0];
  if (!chosen) return undefined;

  return { mac: chosen.mac.toUpperCase(), ip: withIpv4?.address };
}

/** "Windows 11" a partir del número de compilación (Windows 11 es 10.0.22000 o superior). */
export function describeOperatingSystem(osType: string, osRelease: string): string {
  if (osType !== 'Windows_NT') return `${osType} ${osRelease}`;

  const build = Number(osRelease.split('.')[2]);
  const family = Number.isInteger(build) && build >= 22000 ? 'Windows 11' : 'Windows 10';
  return `${family} (${osRelease})`;
}

/** Adaptador físico activo por el que sale la ruta por defecto (o el primero físico activo). */
const PRIMARY_ADAPTER_SCRIPT = `
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$physical = @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up')
$route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Where-Object { $physical.ifIndex -contains $_.ifIndex } |
  Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1
$chosen = if ($route) { $physical | Where-Object ifIndex -eq $route.ifIndex } else { $physical | Sort-Object ifIndex | Select-Object -First 1 }
if ($chosen) { $chosen.Name }
`;

export async function readMachineIdentity(): Promise<MachineIdentity> {
  let preferredName: string | undefined;
  try {
    preferredName = (await runPowerShell(PRIMARY_ADAPTER_SCRIPT)).trim() || undefined;
  } catch {
    // Sin PowerShell o sin NetAdapter: queda la heurística por nombre y prefijo de MAC.
  }

  const primary = pickPrimaryInterface(networkInterfaces(), preferredName);
  if (!primary) throw new Error('No se encontró un adaptador de red físico con MAC para identificar el equipo.');

  return {
    hostname: hostname(),
    macAddress: primary.mac,
    ipAddress: primary.ip,
    operatingSystem: describeOperatingSystem(type(), release()),
  };
}

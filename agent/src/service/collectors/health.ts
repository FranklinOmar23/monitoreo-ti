import type {
  EstadoServicio,
  EventosDatos,
  InicioServicio,
  ProcesosDatos,
  SaludComponente,
  SaludDiscosDatos,
  SeguridadDatos,
  ServiciosDatos,
  VolumenesDatos,
} from '@monitoreoti/shared';
import { runPowerShell } from '../powershell';
import { asArray } from './windows';

// Fase 3: volúmenes, procesos, servicios, eventos, salud de discos y seguridad.
// Scripts de PowerShell 5.1 y parsers puros (se prueban sin Windows).

const PREAMBLE = `$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n`;

const GIB = 1024 ** 3;
const round1 = (value: number) => Math.round(value * 10) / 10;
const num = (value: unknown): number | undefined =>
  typeof value === 'number' && Number.isFinite(value) ? value : undefined;
const text = (value: unknown): string | undefined =>
  typeof value === 'string' && value.trim() ? value.trim() : undefined;

// ------------------------------------------------------------------ volúmenes

export const VOLUMES_SCRIPT = `${PREAMBLE}
Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" |
  Select-Object DeviceID, VolumeName, Size, FreeSpace | ConvertTo-Json -Compress
`;

export function parseVolumes(json: string): VolumenesDatos {
  const volumenes = asArray<Record<string, unknown>>(json)
    .map((v) => ({
      unidad: text(v['DeviceID']) ?? '',
      etiqueta: text(v['VolumeName']),
      total_gb: round1((num(v['Size']) ?? 0) / GIB),
      libre_gb: round1((num(v['FreeSpace']) ?? 0) / GIB),
    }))
    .filter((v) => /^[A-Z]:$/i.test(v.unidad) && v.total_gb > 0);
  return { volumenes };
}

// ------------------------------------------------------------------ procesos

/**
 * Contadores de rendimiento: CPU en % de un núcleo (se divide por los núcleos lógicos) y memoria privada.
 * Una sola consulta, sin tener que tomar dos muestras.
 */
export const PROCESSES_SCRIPT = `${PREAMBLE}
[pscustomobject]@{
  nucleos = [Environment]::ProcessorCount
  procesos = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process |
    Where-Object { $_.Name -ne '_Total' -and $_.Name -ne 'Idle' } |
    Select-Object Name, IDProcess, PercentProcessorTime, WorkingSetPrivate)
} | ConvertTo-Json -Depth 3 -Compress
`;

export const TOP_PROCESSES = 20;

export function parseProcesses(json: string): ProcesosDatos {
  const raw = JSON.parse(json) as { nucleos?: number; procesos?: unknown };
  const cores = Math.max(1, num(raw.nucleos) ?? 1);
  const rows = (Array.isArray(raw.procesos) ? raw.procesos : raw.procesos ? [raw.procesos] : []) as Record<
    string,
    unknown
  >[];

  const procesos = rows
    .map((p) => ({
      // Windows numera las instancias repetidas: chrome, chrome#1, chrome#2…
      nombre: (text(p['Name']) ?? '?').replace(/#\d+$/, ''),
      pid: num(p['IDProcess']) ?? 0,
      cpu: round1(Math.min(100, (num(p['PercentProcessorTime']) ?? 0) / cores)),
      ram_mb: round1((num(p['WorkingSetPrivate']) ?? 0) / 1024 / 1024),
    }))
    .filter((p) => p.pid > 0)
    .sort((a, b) => b.cpu - a.cpu || b.ram_mb - a.ram_mb);

  return { total: procesos.length, procesos: procesos.slice(0, TOP_PROCESSES) };
}

// ------------------------------------------------------------------ servicios

export const SERVICES_SCRIPT = `${PREAMBLE}
Get-CimInstance Win32_Service -Filter "StartMode='Auto'" |
  Select-Object Name, DisplayName, State, StartMode, DelayedAutoStart | ConvertTo-Json -Compress
`;

export function parseServices(json: string): ServiciosDatos {
  const servicios = asArray<Record<string, unknown>>(json)
    .filter((s) => text(s['Name']))
    .map((s) => {
      const state = text(s['State']);
      const estado: EstadoServicio = state === 'Running' ? 'en_ejecucion' : state === 'Stopped' ? 'detenido' : 'otro';
      const mode = text(s['StartMode']);
      const inicio: InicioServicio =
        mode === 'Auto'
          ? s['DelayedAutoStart'] === true
            ? 'automatico_retrasado'
            : 'automatico'
          : mode === 'Manual'
            ? 'manual'
            : 'deshabilitado';
      return { nombre: text(s['Name'])!, nombre_visible: text(s['DisplayName']), estado, inicio };
    });
  return { servicios };
}

// ------------------------------------------------------------------ eventos

/** Último registro enviado de cada log, para no repetir eventos. */
export interface EventBookmark {
  System?: number;
  Application?: number;
  /** ISO UTC desde donde buscar. */
  desde: string;
}

export const MAX_EVENTS_PER_RUN = 100;

/**
 * Errores (2) y críticos (1) de Sistema y Aplicación desde una fecha. Get-WinEvent falla si no hay ninguno:
 * eso no es un error. La fecha la arma el agente (formato ISO fijo), nunca viene de fuera.
 */
export function eventsScript(sinceIsoUtc: string): string {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$/.test(sinceIsoUtc)) throw new Error('Fecha inválida.');
  return `${PREAMBLE}
$desde = [DateTime]::Parse('${sinceIsoUtc}', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToLocalTime()
try {
  Get-WinEvent -FilterHashtable @{ LogName = 'System', 'Application'; Level = 1, 2; StartTime = $desde } -MaxEvents ${MAX_EVENTS_PER_RUN * 2} |
    ForEach-Object {
      [pscustomobject]@{
        registro = $_.LogName; id_registro = $_.RecordId; fecha = $_.TimeCreated.ToUniversalTime().ToString('o')
        origen = $_.ProviderName; codigo = $_.Id; nivel = $_.Level
        mensaje = if ($_.Message) { $_.Message.Substring(0, [Math]::Min(2000, $_.Message.Length)) } else { $null }
      }
    } | ConvertTo-Json -Compress
} catch {
  if ($_.FullyQualifiedErrorId -notmatch 'NoMatchingEventsFound') { throw }
}
`;
}

/** Filtra lo ya enviado y avanza el marcador. */
export function parseEvents(
  json: string,
  bookmark: EventBookmark,
  now: Date,
): { datos: EventosDatos; bookmark: EventBookmark } {
  const next: EventBookmark = { ...bookmark, desde: now.toISOString() };
  const eventos: EventosDatos['eventos'] = [];

  for (const e of asArray<Record<string, unknown>>(json)) {
    const registro =
      e['registro'] === 'Application' ? 'Application' : e['registro'] === 'System' ? 'System' : undefined;
    const id = num(e['id_registro']);
    if (!registro || id === undefined) continue;
    if (bookmark[registro] !== undefined && id <= bookmark[registro]!) continue;

    next[registro] = Math.max(next[registro] ?? 0, id);
    eventos.push({
      registro,
      id_registro: id,
      fecha: text(e['fecha']) ?? now.toISOString(),
      origen: text(e['origen']) ?? 'Desconocido',
      codigo: num(e['codigo']) ?? 0,
      nivel: num(e['nivel']) === 1 ? 'critico' : 'error',
      mensaje: text(e['mensaje']),
    });
  }

  eventos.sort((a, b) => a.fecha.localeCompare(b.fecha));
  return { datos: { eventos: eventos.slice(-MAX_EVENTS_PER_RUN) }, bookmark: next };
}

// ------------------------------------------------------------------ salud de discos

/**
 * Get-PhysicalDisk y sus contadores de confiabilidad sirven para SATA y NVMe. Los sectores reasignados (SMART 5)
 * y pendientes (197) solo existen en discos SATA, en root\wmi; se intentan y, si no hay, se omiten.
 */
export const DISK_HEALTH_SCRIPT = `${PREAMBLE}
$smart = @{}
try {
  Get-CimInstance -Namespace root/wmi -ClassName MSStorageDriver_FailurePredictData -ErrorAction Stop | ForEach-Object {
    $v = $_.VendorSpecific
    $attrs = @{}
    for ($i = 2; $i -lt 362; $i += 12) {
      if ($v[$i] -ne 0) { $attrs[[int]$v[$i]] = [int]$v[$i + 5] + 256 * [int]$v[$i + 6] }
    }
    $smart[$_.InstanceName] = $attrs
  }
} catch { }
$predict = @{}
try {
  Get-CimInstance -Namespace root/wmi -ClassName MSStorageDriver_FailurePredictStatus -ErrorAction Stop |
    ForEach-Object { $predict[$_.InstanceName] = [bool]$_.PredictFailure }
} catch { }
@(Get-PhysicalDisk | ForEach-Object {
  $disk = $_
  $r = $disk | Get-StorageReliabilityCounter -ErrorAction SilentlyContinue
  $key = $smart.Keys | Where-Object { $_ -like ('*' + ($disk.FriendlyName -replace ' ', '_') + '*') } | Select-Object -First 1
  [pscustomobject]@{
    modelo = $disk.FriendlyName; serie = $disk.SerialNumber; tamano = $disk.Size; salud = [string]$disk.HealthStatus
    falla_prevista = if ($key) { $predict[$key] } else { $null }
    reasignados = if ($key) { $smart[$key][5] } else { $null }
    pendientes = if ($key) { $smart[$key][197] } else { $null }
    errores_lectura = $r.ReadErrorsUncorrected; desgaste = $r.Wear; temperatura = $r.Temperature; horas = $r.PowerOnHours
  }
}) | ConvertTo-Json -Compress
`;

export function parseDiskHealth(json: string): SaludDiscosDatos {
  const discos = asArray<Record<string, unknown>>(json).map((d) => {
    const reasignados = num(d['reasignados']);
    const pendientes = num(d['pendientes']);
    const desgaste = num(d['desgaste']);
    const errores = num(d['errores_lectura']);
    const health = text(d['salud']);

    // Windows ya resume la salud (Healthy, Warning, Unhealthy); se endurece con los contadores SMART.
    let salud: SaludComponente =
      health === 'Healthy'
        ? 'sano'
        : health === 'Warning'
          ? 'advertencia'
          : health === 'Unhealthy'
            ? 'critico'
            : 'desconocido';
    if (d['falla_prevista'] === true) salud = 'critico';
    else if (
      salud !== 'critico' &&
      ((reasignados ?? 0) > 0 || (pendientes ?? 0) > 0 || (desgaste ?? 0) >= 90 || (errores ?? 0) > 0)
    )
      salud = 'advertencia';

    return {
      modelo: text(d['modelo']) ?? 'Disco',
      serie: text(d['serie']),
      tamano_gb: num(d['tamano']) ? Math.round(num(d['tamano'])! / GIB) : undefined,
      salud,
      sectores_reasignados: reasignados,
      sectores_pendientes: pendientes,
      errores_lectura: errores,
      desgaste,
      temperatura: num(d['temperatura']) && num(d['temperatura'])! > 0 ? num(d['temperatura']) : undefined,
      horas_encendido: num(d['horas']),
    };
  });
  return { discos };
}

// ------------------------------------------------------------------ seguridad

export const SECURITY_SCRIPT = `${PREAMBLE}
$av = @()
try {
  $av = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
    Select-Object displayName, productState)
} catch { }
$fw = $null
try { $fw = @(Get-NetFirewallProfile -ErrorAction Stop | Where-Object { $_.Enabled }).Count -gt 0 } catch { }
[pscustomobject]@{ antivirus = $av; firewall = $fw } | ConvertTo-Json -Depth 3 -Compress
`;

/**
 * productState del Centro de seguridad: el segundo byte indica si está activo (0x10 o 0x11) y el tercero
 * si las firmas están al día (0x00).
 */
export function decodeProductState(state: number): { activo: boolean; actualizado: boolean } {
  const enabled = (state >> 8) & 0xff;
  const signatures = state & 0xff;
  return { activo: enabled === 0x10 || enabled === 0x11, actualizado: signatures === 0x00 };
}

export function parseSecurity(json: string): SeguridadDatos {
  const raw = JSON.parse(json) as { antivirus?: unknown; firewall?: unknown };
  const list = (Array.isArray(raw.antivirus) ? raw.antivirus : raw.antivirus ? [raw.antivirus] : []) as Record<
    string,
    unknown
  >[];
  return {
    antivirus: list
      .filter((a) => text(a['displayName']))
      .map((a) => ({ nombre: text(a['displayName'])!, ...decodeProductState(num(a['productState']) ?? 0) })),
    firewall_activo: typeof raw.firewall === 'boolean' ? raw.firewall : undefined,
  };
}

// ------------------------------------------------------------------ ejecución

export const collectVolumes = async () => parseVolumes(await runPowerShell(VOLUMES_SCRIPT));
export const collectProcesses = async () =>
  parseProcesses(await runPowerShell(PROCESSES_SCRIPT, { timeoutMs: 60_000 }));
export const collectServices = async () => parseServices(await runPowerShell(SERVICES_SCRIPT, { timeoutMs: 60_000 }));
export const collectDiskHealth = async () =>
  parseDiskHealth(await runPowerShell(DISK_HEALTH_SCRIPT, { timeoutMs: 120_000 }));
export const collectSecurity = async () => parseSecurity(await runPowerShell(SECURITY_SCRIPT, { timeoutMs: 60_000 }));

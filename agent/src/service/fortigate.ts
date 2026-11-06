import http from 'node:http';
import https from 'node:https';

/**
 * API REST del FortiGate (decisión 76), opcional: con un token de solo lectura el recolector lee el DHCP y los
 * dispositivos que el FortiGate identificó. El token llega sellado para este agente, se abre justo antes de usarlo y
 * nunca se escribe en el registro ni vuelve al servidor.
 */

export interface FortiApiConfig {
  url: string;
  /** Token sellado para este agente ({token}). */
  token: string;
  acceptSelfSigned: boolean;
  /** FortiSwitch registrados en Centinela que dependen de este FortiGate (decisión 80). */
  switches?: { id: number; serie: string }[];
}

export interface FortiHttpRequest {
  url: string;
  token: string;
  acceptSelfSigned: boolean;
  timeoutMs: number;
}

export type FortiHttpResponse = { status: number; body: string } | { error: string };

export type FortiHttp = (request: FortiHttpRequest) => Promise<FortiHttpResponse>;

const MAX_BODY = 16 * 1024 * 1024;

/** GET con el token en la cabecera (nunca en la dirección, que podría quedar en un registro del FortiGate). */
export const fortiHttpGet: FortiHttp = (request) =>
  new Promise((resolve) => {
    let target: URL;
    try {
      target = new URL(request.url);
    } catch {
      resolve({ error: 'La dirección de la API no es válida.' });
      return;
    }
    const client = target.protocol === 'http:' ? http : https;
    const req = client.request(
      target,
      {
        method: 'GET',
        headers: { Authorization: `Bearer ${request.token}`, Accept: 'application/json' },
        timeout: request.timeoutMs,
        ...(target.protocol === 'https:' ? { rejectUnauthorized: !request.acceptSelfSigned } : {}),
      },
      (res) => {
        let body = '';
        res.setEncoding('utf8');
        res.on('data', (chunk: string) => {
          body += chunk;
          if (body.length > MAX_BODY) req.destroy(new Error('RESPUESTA_GRANDE'));
        });
        res.on('end', () => resolve({ status: res.statusCode ?? 0, body }));
      },
    );
    req.on('timeout', () => req.destroy(Object.assign(new Error('timeout'), { code: 'ETIMEDOUT' })));
    req.on('error', (error: NodeJS.ErrnoException) => resolve({ error: describeNetworkError(error, target.host) }));
    req.end();
  });

/** Motivo legible de un fallo de conexión (sin datos sensibles). */
export function describeNetworkError(error: NodeJS.ErrnoException, host: string): string {
  const code = error.code ?? '';
  if (code === 'ECONNREFUSED')
    return `${host} rechazó la conexión: revise el puerto y que la interfaz permita HTTPS (allowaccess https).`;
  if (code === 'ETIMEDOUT' || code === 'ESOCKETTIMEDOUT')
    return `${host} no respondió a tiempo: revise la ruta desde el recolector y el puerto de administración.`;
  if (code === 'EHOSTUNREACH' || code === 'ENETUNREACH') return `No hay ruta desde el recolector hasta ${host}.`;
  if (code === 'ENOTFOUND') return `No se encontró el nombre ${host}.`;
  if (code === 'ECONNRESET') return `${host} cortó la conexión (¿HTTP en un puerto HTTPS o al revés?).`;
  if (/CERT|SELF_SIGNED|UNABLE_TO_VERIFY|ERR_TLS/.test(code) || /certificate/i.test(error.message ?? ''))
    return 'El certificado del FortiGate no es de confianza: marque "Aceptar el certificado propio del equipo".';
  if (error.message === 'RESPUESTA_GRANDE') return 'La respuesta de la API es demasiado grande.';
  return `No se pudo conectar a ${host}${code ? ` (${code})` : ''}.`;
}

/** Motivo legible de una respuesta HTTP que no es 200. */
export function describeHttpStatus(status: number): string {
  if (status === 401)
    return 'El FortiGate rechazó el token (401): revise que esté bien copiado y que la IP del recolector esté en "Trusted Hosts" del usuario API.';
  if (status === 403)
    return 'Sin permiso (403): el perfil del usuario API no permite leer esto (necesita lectura en Sistema, Red y Usuarios y dispositivos).';
  if (status === 404) return 'El FortiGate no tiene esa consulta (404): puede ser otra versión de FortiOS.';
  if (status === 429) return 'El FortiGate pidió esperar (429): demasiadas consultas.';
  return `El FortiGate respondió ${status}.`;
}

type JsonValue = Record<string, unknown>;

async function getJson(
  fetch: FortiHttp,
  config: { url: string; token: string; acceptSelfSigned: boolean },
  path: string,
  timeoutMs: number,
): Promise<{ ok: true; json: JsonValue } | { ok: false; error: string; status?: number }> {
  const response = await fetch({
    url: `${config.url.replace(/\/+$/, '')}${path}`,
    token: config.token,
    acceptSelfSigned: config.acceptSelfSigned,
    timeoutMs,
  });
  if ('error' in response) return { ok: false, error: response.error };
  if (response.status !== 200)
    return { ok: false, error: describeHttpStatus(response.status), status: response.status };
  try {
    const json = JSON.parse(response.body) as unknown;
    if (!json || typeof json !== 'object') return { ok: false, error: 'La API no devolvió JSON.' };
    return { ok: true, json: json as JsonValue };
  } catch {
    return {
      ok: false,
      error: 'La API no devolvió JSON (¿es la dirección de administración del FortiGate?).',
    };
  }
}

const text = (value: unknown): string | undefined =>
  typeof value === 'string' && value.trim() !== '' ? value.trim() : undefined;

// ------------------------------------------------------------------ prueba

export interface ApiTestOutcome {
  ok: boolean;
  error?: string;
  modelo?: string;
  version?: string;
  nombre?: string;
  serie?: string;
  ms?: number;
}

/** system/status: { serial, version, build, results: { model_name, model_number, model, hostname } }. */
export function parseStatus(json: JsonValue): Omit<ApiTestOutcome, 'ok' | 'ms'> {
  const results = (json['results'] && typeof json['results'] === 'object' ? json['results'] : {}) as JsonValue;
  const name = text(results['model_name']);
  const number = text(results['model_number']);
  const modelo = name && number ? `${name} ${number}` : (text(results['model']) ?? name);
  const version = text(json['version']);
  const build = typeof json['build'] === 'number' ? ` build ${json['build']}` : '';
  return {
    modelo,
    version: version ? `${version}${build}` : undefined,
    nombre: text(results['hostname']),
    serie: text(json['serial']),
  };
}

/** "Probar API": una consulta a system/status. */
export async function testFortiApi(
  fetch: FortiHttp,
  config: { url: string; token: string; acceptSelfSigned: boolean },
  now: () => number = Date.now,
): Promise<ApiTestOutcome> {
  const started = now();
  const result = await getJson(fetch, config, '/api/v2/monitor/system/status', 15_000);
  const ms = Math.max(0, Math.round(now() - started));
  if (!result.ok) return { ok: false, error: result.error, ms };
  return { ok: true, ...parseStatus(result.json), ms };
}

// ------------------------------------------------------------------ lectura periódica

export interface FortiLease {
  ip: string;
  mac?: string;
  nombre?: string;
  /** Segundos Unix. */
  vence?: number;
  interfaz?: string;
  reservada?: boolean;
}

export interface FortiDhcpServer {
  interfaz?: string;
  rangos: { desde: string; hasta: string }[];
  reservadas: { ip: string; mac?: string; descripcion?: string }[];
}

export interface FortiDevice {
  ip: string;
  mac?: string;
  nombre?: string;
  sistema?: string;
  interfaz?: string;
}

export interface FortiCollectResult {
  ok: boolean;
  error?: string;
  dhcp?: FortiLease[];
  servidoresDhcp?: FortiDhcpServer[];
  dispositivos?: FortiDevice[];
}

const IPV4 = /^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$/;

/** "results" como lista; algunas versiones agrupan por VDOM (lista de listas). */
function rows(json: JsonValue): JsonValue[] {
  const results = json['results'];
  if (!Array.isArray(results)) return [];
  return results
    .flatMap((r: unknown) => (Array.isArray(r) ? r : [r]))
    .filter((r): r is JsonValue => !!r && typeof r === 'object');
}

/** monitor/system/dhcp: concesiones IPv4 vigentes. */
export function parseLeases(json: JsonValue): FortiLease[] {
  const leases: FortiLease[] = [];
  for (const r of rows(json)) {
    const ip = text(r['ip']);
    if (!ip || !IPV4.test(ip)) continue;
    if (r['type'] !== undefined && r['type'] !== 'ipv4') continue;
    const status = text(r['status']);
    if (status && status !== 'leased') continue;
    leases.push({
      ip,
      mac: text(r['mac']),
      nombre: text(r['hostname']),
      vence: typeof r['expire_time'] === 'number' && r['expire_time'] > 0 ? r['expire_time'] : undefined,
      interfaz: text(r['interface']),
      reservada: typeof r['reserved'] === 'boolean' ? r['reserved'] : undefined,
    });
  }
  return leases;
}

/** cmdb/system.dhcp/server: rangos y direcciones reservadas de cada servidor activo. */
export function parseDhcpServers(json: JsonValue): FortiDhcpServer[] {
  const servers: FortiDhcpServer[] = [];
  for (const r of rows(json)) {
    if (r['status'] === 'disable') continue;
    const ranges = Array.isArray(r['ip-range']) ? (r['ip-range'] as JsonValue[]) : [];
    const reserved = Array.isArray(r['reserved-address']) ? (r['reserved-address'] as JsonValue[]) : [];
    servers.push({
      interfaz: text(r['interface']),
      rangos: ranges
        .map((x) => ({ desde: text(x['start-ip']) ?? '', hasta: text(x['end-ip']) ?? '' }))
        .filter((x) => IPV4.test(x.desde) && IPV4.test(x.hasta)),
      reservadas: reserved
        .map((x) => ({ ip: text(x['ip']) ?? '', mac: text(x['mac']), descripcion: text(x['description']) }))
        .filter((x) => IPV4.test(x.ip) && x.ip !== '0.0.0.0'),
    });
  }
  return servers;
}

/** monitor/user/device/query (FortiOS 7) o .../select (6.x): solo los que están en línea. */
export function parseDevices(json: JsonValue): FortiDevice[] {
  const devices: FortiDevice[] = [];
  for (const r of rows(json)) {
    const ip = text(r['ipv4_address']) ?? text(r['ip']);
    if (!ip || !IPV4.test(ip)) continue;
    if (r['is_online'] === false) continue;
    const host = (r['host'] && typeof r['host'] === 'object' ? r['host'] : {}) as JsonValue;
    const os = (r['os'] && typeof r['os'] === 'object' ? r['os'] : {}) as JsonValue;
    const osName = text(r['os_name']) ?? text(os['name']);
    const osVersion = text(r['os_version']) ?? text(os['version']);
    devices.push({
      ip,
      mac: text(r['mac']),
      nombre: text(r['hostname']) ?? text(r['host_name']) ?? text(host['name']),
      sistema: osName ? (osVersion && !osName.includes(osVersion) ? `${osName} ${osVersion}` : osName) : undefined,
      interfaz: text(r['detected_interface']) ?? text(r['interface']),
    });
  }
  return devices;
}

/**
 * Lee el DHCP y los dispositivos. Cada consulta es independiente: si falta un permiso se manda lo demás con el aviso;
 * si fallan todas (token, Trusted Hosts, red) el resultado es un error.
 */
export async function collectFortiGate(
  fetch: FortiHttp,
  config: { url: string; token: string; acceptSelfSigned: boolean },
): Promise<FortiCollectResult> {
  const timeout = 20_000;
  const [leases, servers, firstDevices] = await Promise.all([
    getJson(fetch, config, '/api/v2/monitor/system/dhcp', timeout),
    getJson(fetch, config, '/api/v2/cmdb/system.dhcp/server', timeout),
    getJson(fetch, config, '/api/v2/monitor/user/device/query', timeout),
  ]);
  // FortiOS 6.x no tiene device/query.
  const devices =
    !firstDevices.ok && firstDevices.status === 404
      ? await getJson(fetch, config, '/api/v2/monitor/user/device/select', timeout)
      : firstDevices;

  const parts: [string, typeof leases][] = [
    ['Concesiones DHCP', leases],
    ['Rangos DHCP', servers],
    ['Dispositivos', devices],
  ];
  const failed = parts.filter(([, r]) => !r.ok) as [string, { ok: false; error: string }][];
  if (failed.length === parts.length) return { ok: false, error: failed[0]![1].error };
  return {
    ok: true,
    error: failed.length > 0 ? failed.map(([what, r]) => `${what}: ${r.error}`).join(' ') : undefined,
    dhcp: leases.ok ? parseLeases(leases.json) : undefined,
    servidoresDhcp: servers.ok ? parseDhcpServers(servers.json) : undefined,
    dispositivos: devices.ok ? parseDevices(devices.json) : undefined,
  };
}

/** La API de un dispositivo en la lista del recolector; undefined si no tiene. */
export function parseApiConfig(value: unknown): FortiApiConfig | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const v = value as Record<string, unknown>;
  if (typeof v['url'] !== 'string' || typeof v['token'] !== 'string') return undefined;
  const switches = Array.isArray(v['switches'])
    ? (v['switches'] as unknown[]).filter(
        (s): s is { id: number; serie: string } =>
          !!s &&
          typeof s === 'object' &&
          typeof (s as { id?: unknown }).id === 'number' &&
          typeof (s as { serie?: unknown }).serie === 'string',
      )
    : [];
  return {
    url: v['url'],
    token: v['token'],
    acceptSelfSigned: v['aceptar_certificado'] !== false,
    ...(switches.length > 0 ? { switches } : {}),
  };
}

// ------------------------------------------------------------------ FortiSwitch administrados (decisión 80)

export interface FortiSwitchPort {
  numero: number;
  nombre: string;
  arriba: boolean;
  deshabilitado: boolean;
  velocidad?: number;
  bytesIn?: number;
  bytesOut?: number;
  erroresIn?: number;
  erroresOut?: number;
  descartes?: number;
  /** Enlace con otro FortiSwitch (ISL): su nombre y puerto. */
  isl?: { equipo: string; puerto?: string };
  /** Puerto que sube al FortiGate (FortiLink). */
  fortilink: boolean;
}

export interface FortiSwitchInfo {
  serie: string;
  nombre: string;
  modelo?: string;
  ip?: string;
  conectado: boolean;
  firmware?: string;
  puertos: FortiSwitchPort[];
  macs: { puerto: number; mac: string; vlan?: number }[];
}

export interface FortiSwitchesResult {
  ok: boolean;
  error?: string;
  switches: FortiSwitchInfo[];
  /** Campos que trajo la API (para diagnosticar otra versión de FortiOS). */
  campos: string[];
}

const num = (value: unknown): number | undefined => {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value === 'string' && /^\d+(\.\d+)?$/.test(value.trim())) return Number(value.trim());
  return undefined;
};

/** El primer campo presente entre varios nombres posibles (cambian entre versiones: "rx-bytes" o "rx_bytes"). */
const pick = (o: JsonValue, ...keys: string[]): unknown => {
  for (const key of keys) {
    if (o[key] !== undefined && o[key] !== null) return o[key];
    const alt = key.includes('-') ? key.replaceAll('-', '_') : key.replaceAll('_', '-');
    if (o[alt] !== undefined && o[alt] !== null) return o[alt];
  }
  return undefined;
};

/** "port12" → 12; los puertos que no se llaman así (internal, mgmt) no cuentan. */
export function portNumber(name: string): number | undefined {
  const match = /^port(\d{1,3})$/i.exec(name.trim());
  return match ? Number(match[1]) : undefined;
}

/** "S248EP-v7.2.5-build0453,230227 (GA)" → "S248EP". */
function modelFrom(row: JsonValue, serial: string): string | undefined {
  const explicit = text(pick(row, 'model', 'model_name', 'platform'));
  if (explicit) return explicit;
  const version = text(pick(row, 'os_version', 'version'));
  const prefix = version?.split('-v')[0];
  if (prefix && /^S\d/i.test(prefix)) return prefix;
  return /^S\d{3}/i.test(serial) ? serial.slice(0, 6) : undefined;
}

function parsePort(p: JsonValue): FortiSwitchPort | undefined {
  const name = text(pick(p, 'interface', 'port', 'port_name', 'name'));
  const numero = name ? portNumber(name) : undefined;
  if (!name || numero === undefined) return undefined;
  const status = (text(pick(p, 'status', 'link_status', 'state')) ?? '').toLowerCase();
  const peer = text(pick(p, 'isl_peer_device_name', 'isl-peer-device-name'));
  const speedText = pick(p, 'speed');
  const speed = num(speedText) ?? (typeof speedText === 'string' ? num(/^(\d+)/.exec(speedText)?.[1]) : undefined);
  return {
    numero,
    nombre: name,
    arriba: status === 'up' || status === 'connected',
    deshabilitado: status === 'disabled' || status === 'admin-down' || pick(p, 'admin_status') === 'down',
    velocidad: speed,
    isl: peer ? { equipo: peer, puerto: text(pick(p, 'isl_peer_port_name', 'isl-peer-port-name')) } : undefined,
    fortilink:
      pick(p, 'fortilink_port', 'fgt_peer_port_name', 'fgt-peer-port-name') !== undefined &&
      pick(p, 'fortilink_port') !== false,
  };
}

/** Lo que devuelve managed-switch/status (una fila por switch, con sus puertos). */
export function parseSwitchStatus(json: JsonValue): FortiSwitchInfo[] {
  const switches: FortiSwitchInfo[] = [];
  for (const r of rows(json)) {
    const serial = text(pick(r, 'serial', 'switch-id', 'sn', 'name'));
    if (!serial) continue;
    const state = (text(pick(r, 'status', 'state', 'connection_status')) ?? '').toLowerCase();
    const ports = Array.isArray(r['ports']) ? (r['ports'] as JsonValue[]) : [];
    switches.push({
      serie: serial,
      nombre: text(pick(r, 'switch-id', 'name')) ?? serial,
      modelo: modelFrom(r, serial),
      ip: text(pick(r, 'connecting_from', 'connecting-from', 'ip', 'mgmt_ip')),
      conectado: /connected|online|up|authorized/.test(state) && !/disconnected|offline|down/.test(state),
      firmware: text(pick(r, 'os_version', 'version')),
      puertos: ports
        .map((p) => (p && typeof p === 'object' ? parsePort(p) : undefined))
        .filter((p): p is FortiSwitchPort => !!p)
        .sort((a, b) => a.numero - b.numero),
      macs: [],
    });
  }
  return switches;
}

/** managed-switch/port-stats: contadores por puerto. Lista de {serial, ports: {port1: {...}}} u objeto por serie. */
export function applyPortStats(switches: FortiSwitchInfo[], json: JsonValue): void {
  const results = json['results'];
  const entries: [string | undefined, unknown][] = Array.isArray(results)
    ? results.map((r: unknown) => [
        text(pick((r ?? {}) as JsonValue, 'serial', 'switch-id', 'name')),
        (r as JsonValue)?.['ports'],
      ])
    : results && typeof results === 'object'
      ? Object.entries(results as JsonValue).map(([k, v]) => [k, (v as JsonValue)?.['ports'] ?? v])
      : [];
  for (const [serial, ports] of entries) {
    const sw = switches.find((s) => s.serie === serial || s.nombre === serial);
    if (!sw || !ports || typeof ports !== 'object') continue;
    for (const [name, stats] of Object.entries(ports as JsonValue)) {
      const port = sw.puertos.find((p) => p.nombre === name);
      if (!port || !stats || typeof stats !== 'object') continue;
      const st = stats as JsonValue;
      port.bytesIn = num(pick(st, 'rx-bytes'));
      port.bytesOut = num(pick(st, 'tx-bytes'));
      port.erroresIn = num(pick(st, 'rx-errors'));
      port.erroresOut = num(pick(st, 'tx-errors'));
      const drops = (num(pick(st, 'rx-drops')) ?? 0) + (num(pick(st, 'tx-drops')) ?? 0);
      port.descartes = pick(st, 'rx-drops') === undefined && pick(st, 'tx-drops') === undefined ? undefined : drops;
    }
  }
}

/** detected-device: qué MAC vio cada switch en cada puerto. */
export function applyDetectedDevices(switches: FortiSwitchInfo[], json: JsonValue): void {
  for (const r of rows(json)) {
    const mac = text(pick(r, 'mac'));
    const switchId = text(pick(r, 'switch_id', 'switch-id', 'serial'));
    const portName = text(pick(r, 'port_name', 'port-name', 'port'));
    const sw = switches.find((s) => s.serie === switchId || s.nombre === switchId);
    const numero = portName ? portNumber(portName) : undefined;
    if (!mac || !sw || numero === undefined) continue;
    sw.macs.push({ puerto: numero, mac, vlan: num(pick(r, 'vlan_id', 'vlan')) });
  }
}

/** Lee los FortiSwitch: el estado es obligatorio; contadores y MAC por puerto, si el permiso y la versión los dan. */
export async function readFortiSwitches(
  fetch: FortiHttp,
  config: { url: string; token: string; acceptSelfSigned: boolean },
): Promise<FortiSwitchesResult> {
  const timeout = 20_000;
  let status = await getJson(fetch, config, '/api/v2/monitor/switch-controller/managed-switch/status', timeout);
  if (!status.ok && status.status === 404)
    status = await getJson(fetch, config, '/api/v2/monitor/switch-controller/managed-switch', timeout);
  if (!status.ok) return { ok: false, error: status.error, switches: [], campos: [] };

  const switches = parseSwitchStatus(status.json);
  const first = rows(status.json)[0];
  const firstPort = first && Array.isArray(first['ports']) ? (first['ports'][0] as JsonValue | undefined) : undefined;
  const campos = [
    ...Object.keys(first ?? {}),
    ...Object.keys(firstPort && typeof firstPort === 'object' ? firstPort : {}).map((k) => `puerto.${k}`),
  ];

  const [stats, detected] = await Promise.all([
    getJson(fetch, config, '/api/v2/monitor/switch-controller/managed-switch/port-stats', timeout),
    getJson(fetch, config, '/api/v2/monitor/switch-controller/detected-device', timeout),
  ]);
  if (stats.ok) applyPortStats(switches, stats.json);
  if (detected.ok) applyDetectedDevices(switches, detected.json);
  const missing = [!stats.ok ? 'contadores de puertos' : null, !detected.ok ? 'MAC por puerto' : null].filter(Boolean);
  return {
    ok: true,
    error: missing.length > 0 ? `El FortiGate no dio: ${missing.join(' ni ')}.` : undefined,
    switches,
    campos,
  };
}

/**
 * El "resultado de sondeo" de un FortiSwitch, con el mismo formato que uno por SNMP: así el servidor guarda puertos,
 * errores, MAC por puerto, enlaces y alertas igual que para cualquier switch.
 */
export function switchToSnmpResult(
  deviceId: number,
  serial: string,
  sw: FortiSwitchInfo | undefined,
  fortigateName: string,
  readError?: string,
): Record<string, unknown> {
  if (readError) return { dispositivoId: deviceId, ok: false, error: `No se pudo leer el FortiGate: ${readError}` };
  if (!sw)
    return { dispositivoId: deviceId, ok: false, error: `El FortiGate no administra un switch con serie ${serial}.` };
  if (!sw.conectado)
    return { dispositivoId: deviceId, ok: false, error: 'El FortiGate dice que el switch está desconectado.' };
  return {
    dispositivoId: deviceId,
    ok: true,
    sistema: { descripcion: sw.firmware ?? null, nombre: sw.nombre, uptimeSegundos: null },
    interfaces: sw.puertos.map((p) => ({
      indice: p.numero,
      tipo: 6,
      nombre: p.nombre,
      alias: null,
      admin: p.deshabilitado ? 'down' : 'up',
      operativo: p.arriba ? 'up' : 'down',
      velocidadMbps: p.velocidad ?? null,
      entradaBytes: p.bytesIn ?? null,
      salidaBytes: p.bytesOut ?? null,
      erroresEntrada: p.erroresIn ?? null,
      erroresSalida: p.erroresOut ?? null,
      descartes: p.descartes ?? null,
    })),
    mac: sw.macs.map((m) => ({ ifIndex: m.puerto, mac: m.mac, vlan: m.vlan ?? null })),
    arp: [],
    // Enlaces: otro FortiSwitch por ISL, y el FortiGate por el puerto FortiLink.
    lldp: [
      ...sw.puertos
        .filter((p) => p.isl)
        .map((p) => ({ puertoLocal: p.numero, sistema: p.isl!.equipo, puerto: p.isl!.puerto ?? null })),
      ...sw.puertos
        .filter((p) => p.fortilink && !p.isl)
        .map((p) => ({ puertoLocal: p.numero, sistema: fortigateName, puerto: null })),
    ],
  };
}

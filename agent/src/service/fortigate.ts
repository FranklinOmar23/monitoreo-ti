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
  return { url: v['url'], token: v['token'], acceptSelfSigned: v['aceptar_certificado'] !== false };
}

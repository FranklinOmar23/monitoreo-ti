import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ApiTestInput, ApiTestResult, DeviceDetail } from '@monitoreoti/shared';
import { usePanelApi } from '../../data/PanelData';
import { timeAgo } from '../../data/format';

/** Puerto de administración HTTPS de la empresa de prueba (el estándar es 443). */
export const FORTIGATE_API_PORT = 12443;

export const suggestedApiUrl = (ip: string | null | undefined) =>
  ip?.trim() ? `https://${ip.trim()}:${FORTIGATE_API_PORT}` : null;

/** La API REST se ofrece en firewalls y routers, o donde ya esté configurada. */
export const offersApi = (type: string | undefined, apiUrl?: string | null) =>
  type === 'firewall' || type === 'router' || !!apiUrl;

/** Estado de la API para la ficha del dispositivo. */
export function apiStatusText(device: DeviceDetail): string {
  if (!device.apiUrl) return 'Sin configurar (solo SNMP)';
  if (!device.hasApiToken) return `${device.apiUrl} · sin token`;
  if (device.apiLastError && !device.apiLastOkAt) return `${device.apiUrl} · con error`;
  if (device.apiLastOkAt)
    return `${device.apiUrl} · leída ${timeAgo(device.apiLastOkAt)}${device.apiLastError ? ' (parcial)' : ''}`;
  return `${device.apiUrl} · aún sin leer`;
}

function describe(result: ApiTestResult): string {
  const parts = [result.model, result.version, result.hostname, result.serial ? `serie ${result.serial}` : null].filter(
    Boolean,
  );
  const time = result.milliseconds !== null ? ` en ${result.milliseconds} ms` : '';
  return `Respondió${time}${parts.length > 0 ? `: ${parts.join(' · ')}` : ''}.`;
}

/**
 * "Probar API": el recolector consulta system/status del FortiGate con la dirección y el token indicados (sin token,
 * el guardado). No guarda nada; si se probó lo guardado, la ficha muestra el resultado como estado.
 */
export function ApiTestButton({
  deviceId,
  input,
  disabled,
  label = 'Probar API',
}: {
  deviceId: number;
  input: ApiTestInput;
  disabled?: boolean;
  label?: string;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const test = useMutation({
    mutationFn: () => api.red.testDeviceApi(deviceId, input),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['red', 'dispositivo', deviceId] }),
  });
  const result = test.data;
  return (
    <div className="api-test">
      <button
        type="button"
        className="btn btn-outline"
        disabled={disabled || test.isPending}
        onClick={() => test.mutate()}
        title="El recolector consulta el FortiGate con este token; no se guarda nada"
      >
        {test.isPending ? 'Probando…' : label}
      </button>
      {test.isPending && <span className="muted">Esperando al recolector (hasta 25 s)…</span>}
      {test.error && (
        <span className="api-test-result is-error" role="alert">
          {test.error.message}
        </span>
      )}
      {result && !test.isPending && (
        <span className={`api-test-result ${result.ok ? 'is-ok' : 'is-error'}`} role="status">
          {result.ok ? describe(result) : (result.error ?? 'El FortiGate no respondió.')}
        </span>
      )}
    </div>
  );
}

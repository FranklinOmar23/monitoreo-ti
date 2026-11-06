import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { DetectedSwitch, DetectSwitchesResult, DeviceDetail } from '@monitoreoti/shared';
import { Card } from '@components/Card';
import { usePanelApi } from '../../data/PanelData';

/**
 * "Switches administrados" en la ficha de un FortiGate con API (decisión 80): Detectar pregunta al FortiGate (por el
 * recolector) qué FortiSwitch administra; se eligen los que faltan y se registran en la sucursal del FortiGate. Sus
 * datos llegan después en cada lectura de la API, sin SNMP en los switches.
 */
export function ManagedSwitches({
  device,
  canEdit,
  onOpenDevice,
}: {
  device: DeviceDetail;
  canEdit: boolean;
  onOpenDevice?: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [result, setResult] = useState<DetectSwitchesResult | null>(null);
  const [chosen, setChosen] = useState<Record<string, string>>({});
  const detect = useMutation({
    mutationFn: () => api.red.detectSwitches(device.id),
    onSuccess: (data) => {
      setResult(data);
      // Se proponen los que faltan, con el nombre que les dio el FortiGate.
      setChosen(
        Object.fromEntries(data.switches.filter((s) => s.deviceId === null).map((s) => [s.serial, s.name ?? s.serial])),
      );
    },
  });
  const register = useMutation({
    mutationFn: (list: DetectedSwitch[]) =>
      api.red.registerSwitches(device.id, {
        switches: list.map((s) => ({
          serie: s.serial,
          nombre: (chosen[s.serial] ?? s.name ?? s.serial).trim(),
          modelo: s.model,
          puertos: Math.max(1, s.portCount),
          ip: s.ip,
        })),
      }),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['red'] });
      detect.mutate();
    },
  });

  if (!device.apiUrl || !device.hasApiToken) return null;
  const pending = result?.switches.filter((s) => s.deviceId === null && chosen[s.serial] !== undefined) ?? [];
  const noPorts = result?.ok && result.switches.length > 0 && result.switches.every((s) => s.portCount === 0);

  return (
    <Card
      title="Switches administrados"
      aside={
        canEdit && (
          <button type="button" className="btn btn-outline" disabled={detect.isPending} onClick={() => detect.mutate()}>
            {detect.isPending ? 'Detectando…' : 'Detectar'}
          </button>
        )
      }
    >
      {!result && !detect.isPending && !detect.isError && (
        <p className="muted">
          Los FortiSwitch que administra este FortiGate se leen por su API: no necesitan SNMP ni reglas de firewall.
          Pulse Detectar para ver cuáles hay.
        </p>
      )}
      {detect.isPending && <p className="muted">Preguntando al FortiGate por medio del recolector…</p>}
      {detect.isError && (
        <p className="api-test-result is-error" role="alert">
          {detect.error.message}
        </p>
      )}
      {register.isError && (
        <p className="api-test-result is-error" role="alert">
          {register.error.message}
        </p>
      )}
      {result && !result.ok && (
        <p className="api-test-result is-error" role="alert">
          {result.error ?? 'El FortiGate no respondió.'}
        </p>
      )}
      {result?.ok && result.error && <p className="muted">{result.error}</p>}
      {result?.ok && result.switches.length === 0 && <p className="muted">Este FortiGate no administra FortiSwitch.</p>}
      {result?.ok && result.switches.length > 0 && (
        <table className="data-table">
          <thead>
            <tr>
              {canEdit && <th aria-label="Elegir" />}
              <th>Nombre</th>
              <th>Modelo</th>
              <th>Serie</th>
              <th>IP</th>
              <th>Puertos</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {result.switches.map((s) => {
              const selected = chosen[s.serial] !== undefined;
              return (
                <tr key={s.serial}>
                  {canEdit && (
                    <td>
                      {s.deviceId === null && (
                        <input
                          type="checkbox"
                          aria-label={`Registrar ${s.name ?? s.serial}`}
                          checked={selected}
                          onChange={(e) =>
                            setChosen((c) => {
                              const next = { ...c };
                              if (e.target.checked) next[s.serial] = s.name ?? s.serial;
                              else delete next[s.serial];
                              return next;
                            })
                          }
                        />
                      )}
                    </td>
                  )}
                  <td>
                    {s.deviceId !== null ? (
                      <button type="button" className="link-quiet mono" onClick={() => onOpenDevice?.(s.deviceId!)}>
                        {s.deviceName}
                      </button>
                    ) : selected && canEdit ? (
                      <input
                        className="mono"
                        aria-label={`Nombre para ${s.serial}`}
                        maxLength={100}
                        value={chosen[s.serial]}
                        onChange={(e) => setChosen((c) => ({ ...c, [s.serial]: e.target.value }))}
                      />
                    ) : (
                      <span className="mono">{s.name ?? '—'}</span>
                    )}
                  </td>
                  <td>{s.model ?? '—'}</td>
                  <td className="mono">{s.serial}</td>
                  <td className="mono">{s.ip ?? '—'}</td>
                  <td className="mono">{s.portCount}</td>
                  <td>
                    {s.deviceId !== null ? 'En Centinela' : s.connected ? 'Conectado' : 'Desconectado'}
                    {s.deviceId !== null && !s.connected ? ' · desconectado' : ''}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {noPorts && (
        <p className="muted">
          La API no trajo los puertos. Campos recibidos: <span className="mono">{result!.fields.join(', ')}</span>
        </p>
      )}
      {canEdit && pending.length > 0 && (
        <div className="api-test">
          <button
            type="button"
            className="btn btn-primary"
            disabled={register.isPending || pending.some((s) => !chosen[s.serial]?.trim())}
            onClick={() => register.mutate(pending)}
          >
            {register.isPending
              ? 'Registrando…'
              : `Registrar ${pending.length} ${pending.length === 1 ? 'switch' : 'switches'} en ${device.branchName}`}
          </button>
          <span className="muted">Después se ubican en su gabinete desde la ficha de cada uno.</span>
        </div>
      )}
    </Card>
  );
}

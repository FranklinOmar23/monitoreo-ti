import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Icon } from '@components/Icon';
import { usePanelApi } from '../../data/PanelData';
import { needsTwoFactor, TwoFactorPrompt } from './TwoFactorPrompt';

/** "servicio:Spooler" → "Spooler"; null si la alerta no es de un servicio detenido. */
export function serviceFromAlertCode(code: string | null | undefined): string | null {
  return code?.startsWith('servicio:') ? code.slice('servicio:'.length) || null : null;
}

/**
 * "Iniciar servicio" en una alerta de servicio detenido (decisión 73): manda un comando firmado al agente y muestra el
 * resultado. Si el servicio arranca, la alerta se resuelve sola. Pide el 2FA reciente como cualquier comando.
 */
export function StartServiceButton({ alertId, service }: { alertId: number; service: string }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [batch, setBatch] = useState<string | null>(null);
  const [askCode, setAskCode] = useState(false);
  const start = useMutation({
    mutationFn: () => api.startAlertService(alertId),
    onSuccess: ({ lote }) => setBatch(lote),
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });
  const run = useQuery({
    queryKey: ['ejecuciones', batch],
    queryFn: () => api.remoto.runDetail(batch!),
    enabled: batch !== null,
    // Hasta que el agente responda (o venza el comando).
    refetchInterval: (q) => {
      const status = q.state.data?.commands[0]?.status;
      return status === 'completado' || status === 'error' || status === 'expirado' ? false : 2_000;
    },
  });
  const command = run.data?.commands[0];
  const done = command?.status === 'completado' && command.exitCode === 0;
  const failed =
    command &&
    (command.status === 'error' ||
      command.status === 'expirado' ||
      (command.status === 'completado' && command.exitCode !== 0));

  useEffect(() => {
    if (!done) return;
    // La alerta ya se resolvió en el servidor: se refresca lo que la muestra.
    void queries.invalidateQueries({ queryKey: ['maquina'] });
    void queries.invalidateQueries({ queryKey: ['alertas'] });
  }, [done, queries]);

  const waiting = start.isPending || (batch !== null && !done && !failed);
  return (
    <div className="start-service">
      <button
        type="button"
        className="btn btn-outline btn-sm"
        disabled={waiting || done}
        onClick={() => {
          setBatch(null);
          start.mutate();
        }}
      >
        <Icon name="power" size={14} />
        {done
          ? 'Servicio iniciado'
          : waiting
            ? command?.status === 'pendiente'
              ? 'Esperando al equipo…'
              : 'Iniciando…'
            : `Iniciar ${service}`}
      </button>
      {start.isError && !needsTwoFactor(start.error) && (
        <span className="start-service-error" role="alert">
          {start.error.message}
        </span>
      )}
      {failed && (
        <span className="start-service-error" role="alert">
          {command.status === 'expirado'
            ? 'El equipo no recibió el comando a tiempo.'
            : `No se pudo iniciar: ${command.output?.trim() || 'el servicio no arrancó.'}`}
        </span>
      )}
      {askCode && (
        <TwoFactorPrompt
          onConfirmed={() => {
            setAskCode(false);
            start.mutate();
          }}
          onCancel={() => setAskCode(false)}
        />
      )}
    </div>
  );
}

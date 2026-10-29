import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
import { QueryCache, QueryClient, QueryClientProvider, useQueryClient } from '@tanstack/react-query';
import { ApiError, createPanelApi, type MetricPoint, type PanelApi } from '@monitoreoti/shared';
import { useSession } from '../auth/SessionProvider';

const PanelApiContext = createContext<PanelApi | null>(null);
const RealtimeContext = createContext<Realtime | null>(null);

export function usePanelApi(): PanelApi {
  const api = useContext(PanelApiContext);
  if (!api) throw new Error('usePanelApi debe usarse dentro de PanelDataProvider');
  return api;
}

export function useRealtime(): Realtime | null {
  return useContext(RealtimeContext);
}

/** Guarda la última versión de una función para llamarla desde objetos creados una sola vez. */
class LatestCallback {
  private callback: () => void = () => undefined;

  set(callback: () => void): void {
    this.callback = callback;
  }

  call(): void {
    this.callback();
  }
}

/** Errores que no se arreglan reintentando. */
const isFinal = (error: unknown) => error instanceof ApiError && [400, 401, 403, 404].includes(error.status);

interface PanelDataProviderProps {
  children: ReactNode;
  /** Inyectables para pruebas. */
  api?: PanelApi;
  realtime?: boolean;
}

/**
 * Datos del panel: cliente del API con el token vigente, caché de React Query y tiempo real (SignalR).
 * Un 401 en cualquier consulta significa que la sesión ya no vale: se cierra.
 */
export function PanelDataProvider({ children, api: injectedApi, realtime = true }: PanelDataProviderProps) {
  const { serverUrl, accessToken, logout } = useSession();
  // El QueryClient se crea una vez; la función de cierre de sesión vigente se le pasa por este contenedor.
  const [onUnauthorized] = useState(() => new LatestCallback());
  useEffect(() => onUnauthorized.set(() => void logout()), [onUnauthorized, logout]);

  const [client] = useState(
    () =>
      new QueryClient({
        queryCache: new QueryCache({
          onError: (error) => {
            if (error instanceof ApiError && error.status === 401) onUnauthorized.call();
          },
        }),
        defaultOptions: {
          queries: {
            retry: (count, error) => !isFinal(error) && count < 2,
            staleTime: 15_000,
            refetchOnWindowFocus: false,
          },
        },
      }),
  );

  const api = useMemo(
    () => injectedApi ?? createPanelApi(serverUrl, accessToken),
    [injectedApi, serverUrl, accessToken],
  );

  return (
    <QueryClientProvider client={client}>
      <PanelApiContext.Provider value={api}>
        {realtime ? <RealtimeProvider serverUrl={serverUrl}>{children}</RealtimeProvider> : children}
      </PanelApiContext.Provider>
    </QueryClientProvider>
  );
}

type MetricListener = (point: MetricPoint) => void;

/** Conexión en vivo con el servidor. */
export interface Realtime {
  /** Recibe las métricas nuevas de un equipo mientras su ficha está abierta. Devuelve la función para dejar de recibir. */
  watchMachine(machineId: number, listener: MetricListener): () => void;
}

function RealtimeProvider({ serverUrl, children }: { serverUrl: string; children: ReactNode }) {
  const { accessToken } = useSession();
  const queries = useQueryClient();
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const listeners = useRef(new Map<number, Set<MetricListener>>());
  // El token se renueva cada 15 min; la conexión lo pide al reconectar sin tener que recrearse.
  const tokenRef = useRef(accessToken);
  useEffect(() => {
    tokenRef.current = accessToken;
  }, [accessToken]);

  useEffect(() => {
    const hub = new HubConnectionBuilder()
      .withUrl(`${serverUrl.replace(/\/+$/, '')}/hubs/tiempo-real`, {
        accessTokenFactory: () => tokenRef.current() ?? '',
        // La sesión va en el token, no en cookies: sin credenciales el CORS del servidor puede seguir sin AllowCredentials.
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    // Un equipo cambió de estado: se refrescan las listas, el dashboard y su ficha.
    hub.on('maquinaEstadoCambio', (event: { maquinaId: number }) => {
      void queries.invalidateQueries({ queryKey: ['maquinas'] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
      void queries.invalidateQueries({ queryKey: ['sucursales'] });
      void queries.invalidateQueries({ queryKey: ['maquina', event.maquinaId] });
    });

    // Una alerta se abrió, cambió o se resolvió: listas, contadores, su detalle y la ficha del equipo.
    const alertChanged = (event: { alertaId: number; maquinaId: number | null }) => {
      void queries.invalidateQueries({ queryKey: ['alertas'] });
      void queries.invalidateQueries({ queryKey: ['alerta', event.alertaId] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
      if (event.maquinaId !== null) void queries.invalidateQueries({ queryKey: ['maquina', event.maquinaId] });
    };
    hub.on('alertaNueva', alertChanged);
    hub.on('alertaActualizada', alertChanged);

    hub.on('ticketActualizado', (event: { ticketId: number }) => {
      void queries.invalidateQueries({ queryKey: ['tickets'] });
      void queries.invalidateQueries({ queryKey: ['ticket', event.ticketId] });
      void queries.invalidateQueries({ queryKey: ['dashboard'] });
    });

    hub.on(
      'metricasNuevas',
      (event: {
        maquinaId: number;
        fecha: string;
        cpu: number | null;
        ram: number | null;
        disco: number | null;
        temperatura: number | null;
      }) => {
        const point: MetricPoint = {
          at: event.fecha,
          cpu: event.cpu,
          ram: event.ram,
          disk: event.disco,
          temperature: event.temperatura,
        };
        listeners.current.get(event.maquinaId)?.forEach((listener) => listener(point));
      },
    );

    // Al reconectar se pierden los grupos de máquinas: se vuelve a suscribir a las fichas abiertas.
    hub.onreconnected(() => {
      for (const machineId of listeners.current.keys())
        void hub.invoke('SuscribirMaquina', machineId).catch(() => undefined);
    });

    // withAutomaticReconnect solo cubre caídas después de conectar: el primer intento se reintenta aquí
    // (por ejemplo, si el panel abre antes que el servidor). Sin tiempo real el panel sigue con consultas normales.
    let cancelled = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const start = (attempt: number) => {
      hub
        .start()
        .then(() => {
          if (cancelled) return hub.stop();
          setConnection(hub);
          for (const machineId of listeners.current.keys())
            void hub.invoke('SuscribirMaquina', machineId).catch(() => undefined);
        })
        .catch(() => {
          if (!cancelled) retry = setTimeout(() => start(attempt + 1), Math.min(30_000, 2_000 * 2 ** attempt));
        });
    };
    start(0);

    return () => {
      cancelled = true;
      clearTimeout(retry);
      setConnection(null);
      void hub.stop();
    };
  }, [serverUrl, queries]);

  const value = useMemo<Realtime>(
    () => ({
      watchMachine(machineId, listener) {
        let set = listeners.current.get(machineId);
        if (!set) {
          set = new Set();
          listeners.current.set(machineId, set);
          if (connection?.state === HubConnectionState.Connected)
            void connection.invoke('SuscribirMaquina', machineId).catch(() => undefined);
        }
        set.add(listener);

        return () => {
          set.delete(listener);
          if (set.size === 0) {
            listeners.current.delete(machineId);
            if (connection?.state === HubConnectionState.Connected)
              void connection.invoke('DesuscribirMaquina', machineId).catch(() => undefined);
          }
        };
      },
    }),
    [connection],
  );

  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>;
}

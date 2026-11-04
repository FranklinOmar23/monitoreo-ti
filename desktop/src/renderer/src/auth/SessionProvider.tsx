import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiError, createAuthApi, type AuthApi, type CurrentSession, type SessionTokens } from '@monitoreoti/shared';
import { cachedAppearance } from '../theme/brand';

export type SessionState =
  | { status: 'loading' }
  | { status: 'anonymous'; notice?: string }
  | { status: 'authenticated'; session: CurrentSession; accessTokenExpira: string };

interface SessionContextValue {
  state: SessionState;
  api: AuthApi;
  /** URL del servidor (la misma que permite la CSP); la usan el cliente del panel y SignalR. */
  serverUrl: string;
  /** Termina el login: guarda el refresh cifrado y carga los datos de la sesión. */
  completeLogin(tokens: SessionTokens): Promise<void>;
  logout(): Promise<void>;
  switchCompany(companyId: number): Promise<void>;
  /** Vuelve a leer los datos de la sesión (por ejemplo, tras cambiar el nombre en Mi perfil). */
  reloadSession(): Promise<void>;
  /** Token de acceso vigente para llamar al API (se renueva solo antes de vencer). */
  accessToken(): string | null;
}

const SessionContext = createContext<SessionContextValue | null>(null);

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (!value) throw new Error('useSession debe usarse dentro de SessionProvider');
  return value;
}

const DEFAULT_SERVER = 'http://localhost:5126';
/** Se renueva un minuto antes de que venza el token de acceso (15 min). */
const REFRESH_MARGIN_MS = 60_000;
/** Espera antes de reintentar cuando el servidor no responde. */
const RETRY_MS = 10_000;

/** Fallo de red o del servidor (no un rechazo de la sesión): vale la pena reintentar. */
function isTransient(error: unknown): boolean {
  return !(error instanceof ApiError) || error.status === 0 || error.status >= 500;
}

/** Tokens de la sesión. El de acceso vive solo en memoria; no forman parte del estado que se dibuja. */
class TokenHolder {
  access: string | null = null;
  refresh: string | null = null;

  set(tokens: SessionTokens): void {
    this.access = tokens.accessToken;
    this.refresh = tokens.refreshToken;
  }

  clear(): void {
    this.access = null;
    this.refresh = null;
  }
}

interface SessionProviderProps {
  children: ReactNode;
  /** Inyectable para pruebas. */
  api?: AuthApi;
}

/**
 * Sesión del panel. El refresh se guarda cifrado por el proceso principal (safeStorage) para no pedir la
 * contraseña cada vez que se abre el panel.
 */
export function SessionProvider({ children, api: injectedApi }: SessionProviderProps) {
  // Sin puente (pruebas, navegador) se usa el servidor por defecto; con puente, el que indique el proceso principal.
  const [serverUrl, setServerUrl] = useState<string | null>(() =>
    injectedApi || !window.monitoreo ? DEFAULT_SERVER : null,
  );
  const api = useMemo(() => injectedApi ?? (serverUrl ? createAuthApi(serverUrl) : null), [injectedApi, serverUrl]);
  const [state, setState] = useState<SessionState>({ status: 'loading' });
  const [tokens] = useState(() => new TokenHolder());

  useEffect(() => {
    if (serverUrl) return;
    window.monitoreo
      ?.getAppInfo()
      .then((info) => setServerUrl(info.serverUrl))
      .catch(() => setServerUrl(DEFAULT_SERVER));
  }, [serverUrl]);

  const clear = useCallback(
    async (notice?: string) => {
      tokens.clear();
      await window.monitoreo?.session.clear().catch(() => undefined);
      setState({ status: 'anonymous', notice });
    },
    [tokens],
  );

  const adopt = useCallback(
    async (currentApi: AuthApi, next: SessionTokens) => {
      tokens.set(next);
      await window.monitoreo?.session.save(next.refreshToken).catch(() => undefined);
      const session = await currentApi.session(next.accessToken);
      setState({ status: 'authenticated', session, accessTokenExpira: next.accessTokenExpira });
    },
    [tokens],
  );

  // Al abrir el panel: si hay una sesión guardada, se renueva sin pedir la contraseña.
  // Si el servidor no responde, se reintenta sin borrar la sesión guardada.
  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    let retry: ReturnType<typeof setTimeout> | undefined;
    const restore = async () => {
      const stored = await window.monitoreo?.session.load().catch(() => null);
      if (!stored) {
        if (!cancelled) setState({ status: 'anonymous' });
        return;
      }
      try {
        const next = await api.refresh(stored);
        if (!cancelled) await adopt(api, next);
      } catch (error) {
        if (cancelled) return;
        if (isTransient(error)) retry = setTimeout(() => void restore(), RETRY_MS);
        else await clear();
      }
    };
    void restore();
    return () => {
      cancelled = true;
      clearTimeout(retry);
    };
  }, [api, adopt, clear]);

  // Renovación programada: cada token de acceso nuevo agenda la siguiente renovación.
  // Un corte de red o del servidor no cierra la sesión: se reintenta. Solo un rechazo real (401) la cierra.
  const expiresAt = state.status === 'authenticated' ? state.accessTokenExpira : null;
  useEffect(() => {
    if (!api || !expiresAt) return;
    let timer: ReturnType<typeof setTimeout>;
    const renew = () => {
      const refresh = tokens.refresh;
      if (!refresh) return;
      api
        .refresh(refresh)
        .then((next) => adopt(api, next))
        .catch((error: unknown) => {
          if (isTransient(error)) timer = setTimeout(renew, RETRY_MS);
          else void clear('La sesión venció. Inicie sesión de nuevo.');
        });
    };
    timer = setTimeout(renew, Math.max(5_000, new Date(expiresAt).getTime() - Date.now() - REFRESH_MARGIN_MS));
    return () => clearTimeout(timer);
  }, [api, expiresAt, adopt, clear, tokens]);

  const value = useMemo<SessionContextValue | null>(() => {
    if (!api || !serverUrl) return null;
    return {
      state,
      api,
      serverUrl,
      completeLogin: (next) => adopt(api, next),
      logout: async () => {
        if (tokens.refresh) await api.logout(tokens.refresh).catch(() => undefined);
        await clear();
      },
      switchCompany: async (companyId) => {
        if (!tokens.access) return;
        const previousRefresh = tokens.refresh;
        await adopt(api, await api.switchCompany(tokens.access, companyId));
        if (previousRefresh) await api.logout(previousRefresh).catch(() => undefined);
      },
      reloadSession: async () => {
        if (!tokens.access) return;
        const session = await api.session(tokens.access);
        setState((current) => (current.status === 'authenticated' ? { ...current, session } : current));
      },
      accessToken: () => tokens.access,
    };
  }, [api, serverUrl, state, adopt, clear, tokens]);

  if (!value) return <SplashScreen />;
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function SplashScreen() {
  // El logo de la última empresa vista (W-40): todavía no se sabe la sesión.
  const logo = cachedAppearance()?.logo;
  return (
    <div className="splash" role="status" aria-live="polite">
      {logo ? <img className="splash-logo" src={logo} alt="" /> : <span className="splash-mark" aria-hidden="true" />}
      <span>Cargando…</span>
    </div>
  );
}

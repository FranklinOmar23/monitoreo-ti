import { useState, type FormEvent, type ReactNode } from 'react';
import {
  ApiError,
  type AuthApi,
  type CompanyOption,
  type LoginResponse,
  type SessionTokens,
  type TwoFactorSetup,
} from '@monitoreoti/shared';
import { Icon } from '@components/Icon';
import { useDeepLink } from '../data/deepLinks';
import { OTP_LENGTH, OtpInput, successPause, VerifyBadge, type OtpState } from '@components/OtpInput';

type Step =
  | { kind: 'login' }
  | { kind: 'verify'; challenge: string }
  | { kind: 'setup'; challenge: string; setup: TwoFactorSetup }
  | { kind: 'company'; challenge: string; companies: CompanyOption[] }
  | { kind: 'recover' }
  | { kind: 'recover-sent'; email: string }
  | { kind: 'reset'; link?: string };

interface LoginScreenProps {
  api: AuthApi;
  onSession: (tokens: SessionTokens) => Promise<void>;
  /** Aviso al llegar (por ejemplo, "la sesión venció"). */
  notice?: string;
}

/** W-01 Login, W-02 Verificación 2FA (y su alta) y W-03 Recuperar contraseña. */
export function LoginScreen({ api, onSession, notice }: LoginScreenProps) {
  const [step, setStep] = useState<Step>({ kind: 'login' });
  const [error, setError] = useState<string | undefined>();
  const [info, setInfo] = useState<string | undefined>(notice);
  const [busy, setBusy] = useState(false);

  // Enlace monitoreoti://restablecer del correo: abre "Contraseña nueva" con el enlace ya puesto.
  useDeepLink((link) => {
    if (link.kind !== 'restablecer') return false;
    setError(undefined);
    setStep({ kind: 'reset', link: `monitoreoti://restablecer?token=${link.token}` });
    return true;
  });

  /** Ejecuta una llamada mostrando el estado ocupado y el mensaje de error del servidor. */
  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(undefined);
    try {
      await action();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Ocurrió un error inesperado. Intente de nuevo.');
      // Un desafío vencido obliga a empezar de nuevo.
      if (e instanceof ApiError && e.codigo === 'desafio_invalido') setStep({ kind: 'login' });
    } finally {
      setBusy(false);
    }
  };

  const advance = async (response: LoginResponse) => {
    setInfo(undefined);
    switch (response.paso) {
      case 'completado':
        if (response.sesion) await onSession(response.sesion);
        return;
      case 'verificar-2fa':
        setStep({ kind: 'verify', challenge: response.desafio ?? '' });
        return;
      case 'activar-2fa':
        if (response.activacion2fa)
          setStep({ kind: 'setup', challenge: response.desafio ?? '', setup: response.activacion2fa });
        return;
      case 'elegir-empresa':
        setStep({ kind: 'company', challenge: response.desafio ?? '', companies: response.empresas ?? [] });
        return;
    }
  };

  const back = () => {
    setError(undefined);
    setStep({ kind: 'login' });
  };

  /** Comprueba un código; undefined si el servidor lo rechazó (el mensaje queda en pantalla). */
  const check = async (call: () => Promise<LoginResponse>) => {
    let response: LoginResponse | undefined;
    await run(async () => {
      response = await call();
    });
    return response;
  };

  const finish = (response: LoginResponse) => run(() => advance(response));

  let content: ReactNode;
  switch (step.kind) {
    case 'login':
      content = (
        <LoginForm
          busy={busy}
          onSubmit={(email, password) => run(async () => advance(await api.login(email, password)))}
          onForgot={() => {
            setError(undefined);
            setStep({ kind: 'recover' });
          }}
        />
      );
      break;
    case 'verify':
      content = (
        <CodeForm
          title="Verificación en dos pasos"
          description="Escriba el código de 6 dígitos que muestra su app autenticadora."
          onCheck={(code) => check(() => api.verifyTwoFactor(step.challenge, code))}
          onVerified={finish}
          onBack={back}
        />
      );
      break;
    case 'setup':
      content = (
        <CodeForm
          title="Active la verificación en dos pasos"
          description="Su rol permite controlar equipos a distancia, así que la cuenta necesita un segundo factor."
          onCheck={(code) => check(() => api.confirmTwoFactorSetup(step.challenge, code))}
          onVerified={finish}
          onBack={back}
        >
          <ol className="auth-steps">
            <li>
              <span className="auth-step-number">1</span>
              <div>
                <strong>Escanee el código</strong>
                <p className="muted">Con Google Authenticator, Microsoft Authenticator u otra app compatible.</p>
              </div>
            </li>
          </ol>
          <div className="auth-qr">
            <img src={step.setup.qr} alt="Código QR para la app autenticadora" width={160} height={160} />
            <div>
              <span className="eyebrow">¿No puede escanear?</span>
              <p className="muted">Escriba esta clave en la app:</p>
              <code className="auth-secret mono">{step.setup.secreto.match(/.{1,4}/g)?.join(' ')}</code>
              <CopyButton text={step.setup.secreto} />
            </div>
          </div>
          <ol className="auth-steps" start={2}>
            <li>
              <span className="auth-step-number">2</span>
              <div>
                <strong>Escriba el código que aparece</strong>
                <p className="muted">Cambia cada 30 segundos.</p>
              </div>
            </li>
          </ol>
        </CodeForm>
      );
      break;
    case 'company':
      content = (
        <div className="auth-form">
          <h1>Elija la empresa</h1>
          <p className="muted">Su usuario tiene acceso a varias empresas. Puede cambiar después desde su perfil.</p>
          <ul className="auth-companies">
            {step.companies.map((company) => (
              <li key={company.id}>
                <button
                  type="button"
                  className="auth-company"
                  disabled={busy}
                  onClick={() => run(async () => advance(await api.selectCompany(step.challenge, company.id)))}
                >
                  <span className="auth-company-icon" aria-hidden="true">
                    <Icon name="building" />
                  </span>
                  <span>
                    <strong>{company.nombre}</strong>
                    <span className="muted">{company.rol}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
          <button type="button" className="link" onClick={back}>
            Volver
          </button>
        </div>
      );
      break;
    case 'recover':
      content = (
        <RecoverForm
          busy={busy}
          onSubmit={(email) =>
            run(async () => {
              await api.requestPasswordReset(email);
              setStep({ kind: 'recover-sent', email });
            })
          }
          onBack={back}
        />
      );
      break;
    case 'recover-sent':
      content = (
        <div className="auth-form">
          <h1>Revise su correo</h1>
          <p className="muted">
            Si <strong>{step.email}</strong> tiene una cuenta, le enviamos un enlace para crear una contraseña nueva.
            Vence en 30 minutos.
          </p>
          <button type="button" className="btn btn-primary auth-submit" onClick={() => setStep({ kind: 'reset' })}>
            Ya tengo el enlace
          </button>
          <button type="button" className="link" onClick={back}>
            Volver al inicio de sesión
          </button>
        </div>
      );
      break;
    case 'reset':
      content = (
        <ResetForm
          key={step.link}
          initialLink={step.link}
          busy={busy}
          onSubmit={(token, password) =>
            run(async () => {
              await api.resetPassword(token, password);
              setStep({ kind: 'login' });
              setInfo('Contraseña cambiada. Ya puede iniciar sesión.');
            })
          }
          onBack={back}
        />
      );
      break;
  }

  return (
    <div className="auth">
      <aside className="auth-brand">
        <div className="sidebar-brand">
          <span className="brand-mark">
            <Icon name="logo" size={20} />
          </span>
          <span className="brand-name">Centinela</span>
        </div>
        <div className="auth-pitch">
          <h2>Todos sus equipos, en un solo lugar.</h2>
          <p>Estado en vivo de cada computadora, servidor y switch de todas sus sucursales.</p>
        </div>
        <span className="auth-foot">MonitoreoTI</span>
      </aside>
      <main className="auth-main">
        <div className="auth-card">
          {info && (
            <p className="auth-info" role="status">
              {info}
            </p>
          )}
          {error && (
            <p className="auth-error" role="alert">
              {error}
            </p>
          )}
          {/* La clave cambia con el paso: el nuevo entra deslizándose. */}
          <div key={step.kind} className="auth-step">
            {content}
          </div>
        </div>
      </main>
    </div>
  );
}

function LoginForm({
  busy,
  onSubmit,
  onForgot,
}: {
  busy: boolean;
  onSubmit: (email: string, password: string) => void;
  onForgot: () => void;
}) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    onSubmit(email.trim(), password);
  };

  return (
    <form className="auth-form" onSubmit={submit}>
      <h1>Iniciar sesión</h1>
      <p className="muted">Entre con su correo de trabajo.</p>
      <label className="field">
        <span>Correo</span>
        <input
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          autoFocus
        />
      </label>
      <label className="field">
        <span>Contraseña</span>
        <input
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
      </label>
      <button type="submit" className="btn btn-primary auth-submit" disabled={busy}>
        {busy ? 'Entrando…' : 'Entrar'}
      </button>
      <button type="button" className="link" onClick={onForgot}>
        ¿Olvidó su contraseña?
      </button>
    </form>
  );
}

function CodeForm({
  title,
  description,
  onCheck,
  onVerified,
  onBack,
  children,
}: {
  title: string;
  description: string;
  onCheck: (code: string) => Promise<LoginResponse | undefined>;
  onVerified: (response: LoginResponse) => Promise<void>;
  onBack: () => void;
  children?: ReactNode;
}) {
  const [code, setCode] = useState('');
  const [state, setState] = useState<OtpState>('idle');
  // Cambia con cada código rechazado: las casillas se montan de nuevo (sacudida y foco en la primera).
  const [attempt, setAttempt] = useState(0);
  const digits = code.replace(/ /g, '');

  const verify = async (value: string) => {
    setState('checking');
    const response = await onCheck(value);
    if (!response) {
      setState('error');
      setCode('');
      setAttempt((n) => n + 1);
      return;
    }
    setState('success');
    await new Promise((resolve) => setTimeout(resolve, successPause()));
    await onVerified(response);
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (digits.length === OTP_LENGTH && state !== 'checking' && state !== 'success') void verify(digits);
  };

  return (
    <form className="auth-form auth-verify" onSubmit={submit}>
      <VerifyBadge state={state} />
      <h1>{title}</h1>
      <p className="muted">{description}</p>
      {children}
      <OtpInput
        key={attempt}
        value={code}
        onChange={(next) => {
          setCode(next);
          if (state === 'error') setState('idle');
        }}
        onComplete={(value) => void verify(value)}
        state={state}
        autoFocus
      />
      <p className="otp-status" role="status">
        {state === 'checking' && 'Verificando…'}
        {state === 'success' && 'Código correcto'}
      </p>
      <button
        type="submit"
        className="btn btn-primary auth-submit"
        disabled={digits.length !== OTP_LENGTH || state === 'checking' || state === 'success'}
      >
        {state === 'checking' ? 'Verificando…' : 'Verificar'}
      </button>
      <button type="button" className="link" onClick={onBack} disabled={state === 'checking' || state === 'success'}>
        Volver
      </button>
    </form>
  );
}

/** Copia la clave del 2FA y lo confirma un momento. */
function CopyButton({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className="btn btn-outline auth-copy"
      data-copied={copied || undefined}
      onClick={() => {
        void navigator.clipboard?.writeText(text).then(() => {
          setCopied(true);
          setTimeout(() => setCopied(false), 1800);
        });
      }}
    >
      {copied ? (
        <>
          <Icon name="check" size={14} /> Copiada
        </>
      ) : (
        'Copiar clave'
      )}
    </button>
  );
}

function RecoverForm({
  busy,
  onSubmit,
  onBack,
}: {
  busy: boolean;
  onSubmit: (email: string) => void;
  onBack: () => void;
}) {
  const [email, setEmail] = useState('');

  return (
    <form
      className="auth-form"
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit(email.trim());
      }}
    >
      <h1>Recuperar contraseña</h1>
      <p className="muted">Le enviaremos un enlace para crear una contraseña nueva.</p>
      <label className="field">
        <span>Correo</span>
        <input type="email" required value={email} onChange={(e) => setEmail(e.target.value)} autoFocus />
      </label>
      <button type="submit" className="btn btn-primary auth-submit" disabled={busy}>
        {busy ? 'Enviando…' : 'Enviar enlace'}
      </button>
      <button type="button" className="link" onClick={onBack}>
        Volver
      </button>
    </form>
  );
}

/** Acepta el enlace completo del correo o solo el token. */
export function extractResetToken(value: string): string {
  const trimmed = value.trim();
  const match = /[?&]token=([A-Za-z0-9_-]+)/.exec(trimmed);
  return match?.[1] ?? trimmed;
}

function ResetForm({
  initialLink,
  busy,
  onSubmit,
  onBack,
}: {
  initialLink?: string;
  busy: boolean;
  onSubmit: (token: string, password: string) => void;
  onBack: () => void;
}) {
  const [link, setLink] = useState(initialLink ?? '');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const mismatch = confirm.length > 0 && password !== confirm;

  return (
    <form
      className="auth-form"
      onSubmit={(event) => {
        event.preventDefault();
        if (!mismatch) onSubmit(extractResetToken(link), password);
      }}
    >
      <h1>Contraseña nueva</h1>
      <p className="muted">Pegue el enlace que recibió por correo y elija una contraseña nueva.</p>
      <label className="field">
        <span>Enlace del correo</span>
        <input required value={link} onChange={(e) => setLink(e.target.value)} autoFocus />
      </label>
      {/* Las pistas van fuera de la etiqueta (aria-describedby) para no alargar el nombre del campo. */}
      <div className="field">
        <label htmlFor="reset-password">Contraseña nueva</label>
        <input
          id="reset-password"
          type="password"
          autoComplete="new-password"
          required
          minLength={10}
          aria-describedby="reset-password-hint"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <small id="reset-password-hint" className="muted">
          Mínimo 10 caracteres, con letras y números.
        </small>
      </div>
      <div className="field">
        <label htmlFor="reset-confirm">Repita la contraseña</label>
        <input
          id="reset-confirm"
          type="password"
          autoComplete="new-password"
          required
          value={confirm}
          aria-invalid={mismatch}
          aria-describedby={mismatch ? 'reset-confirm-error' : undefined}
          onChange={(e) => setConfirm(e.target.value)}
        />
        {mismatch && (
          <small id="reset-confirm-error" className="field-error">
            Las contraseñas no coinciden.
          </small>
        )}
      </div>
      <button type="submit" className="btn btn-primary auth-submit" disabled={busy || mismatch}>
        {busy ? 'Guardando…' : 'Cambiar contraseña'}
      </button>
      <button type="button" className="link" onClick={onBack}>
        Volver
      </button>
    </form>
  );
}

import { useRef, type ClipboardEvent, type CSSProperties, type KeyboardEvent } from 'react';
import { Icon } from './Icon';

export const OTP_LENGTH = 6;

/** idle: escribiendo · checking: el servidor lo comprueba · error: código rechazado · success: aceptado. */
export type OtpState = 'idle' | 'checking' | 'error' | 'success';

/** Pausa para que se vea la confirmación antes de seguir; sin animaciones no se espera. */
export function successPause(): number {
  const reduced = typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
  return typeof matchMedia === 'function' && !reduced ? 650 : 0;
}

interface OtpInputProps {
  value: string;
  onChange: (value: string) => void;
  /** Al llenar las seis casillas: el código se envía solo. */
  onComplete: (code: string) => void;
  state?: OtpState;
  label?: string;
  autoFocus?: boolean;
}

/**
 * Código de la app autenticadora en seis casillas (W-02): el cursor avanza solo, Retroceso vuelve, las flechas mueven y
 * pegar (o el autocompletado del sistema) llena todas. Para repetir la sacudida de un código rechazado, el padre cambia
 * la `key` y el componente se monta de nuevo con el foco en la primera casilla.
 */
export function OtpInput({
  value,
  onChange,
  onComplete,
  state = 'idle',
  label = 'Código de 6 dígitos',
  autoFocus,
}: OtpInputProps) {
  const boxes = useRef<(HTMLInputElement | null)[]>([]);
  const locked = state === 'checking' || state === 'success';

  const focus = (index: number) => {
    const box = boxes.current[Math.max(0, Math.min(OTP_LENGTH - 1, index))];
    box?.focus();
    box?.select();
  };

  /** Escribe dígitos desde una casilla (uno al teclear, varios al pegar) y deja el cursor en la siguiente vacía. */
  const write = (index: number, digits: string) => {
    if (locked) return;
    const clean = digits.replace(/\D/g, '');
    if (!clean) return;
    const chars = value.padEnd(OTP_LENGTH, ' ').split('');
    // El autocompletado del sistema mete el código entero en la primera casilla: se reparte desde el principio.
    const start = clean.length >= OTP_LENGTH ? 0 : index;
    clean
      .slice(0, OTP_LENGTH - start)
      .split('')
      .forEach((digit, i) => (chars[start + i] = digit));
    const next = chars.join('').replace(/ +$/, '');
    onChange(next);
    const filled = next.replace(/ /g, '');
    if (filled.length === OTP_LENGTH) {
      boxes.current[OTP_LENGTH - 1]?.blur();
      onComplete(filled);
    } else focus(Math.min(start + clean.length, OTP_LENGTH - 1));
  };

  const clear = (index: number) => {
    const chars = value.padEnd(OTP_LENGTH, ' ').split('');
    chars[index] = ' ';
    onChange(chars.join('').replace(/ +$/, ''));
  };

  const keyDown = (index: number, event: KeyboardEvent<HTMLInputElement>) => {
    if (locked) return;
    if (event.key === 'Backspace') {
      event.preventDefault();
      if (value[index] && value[index] !== ' ') clear(index);
      else if (index > 0) {
        clear(index - 1);
        focus(index - 1);
      }
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      focus(index - 1);
    } else if (event.key === 'ArrowRight') {
      event.preventDefault();
      focus(index + 1);
    }
  };

  const paste = (index: number, event: ClipboardEvent<HTMLInputElement>) => {
    event.preventDefault();
    write(index, event.clipboardData.getData('text'));
  };

  return (
    <div className="otp" role="group" aria-label={label} data-state={state}>
      {Array.from({ length: OTP_LENGTH }, (_, index) => {
        const digit = value[index] && value[index] !== ' ' ? value[index] : '';
        return (
          <input
            key={index}
            ref={(el) => {
              boxes.current[index] = el;
            }}
            className="otp-box mono"
            style={{ '--i': index } as CSSProperties}
            data-filled={digit ? 'true' : undefined}
            inputMode="numeric"
            autoComplete={index === 0 ? 'one-time-code' : 'off'}
            aria-label={`Dígito ${index + 1} de ${OTP_LENGTH}`}
            aria-invalid={state === 'error' || undefined}
            readOnly={locked}
            value={digit}
            autoFocus={autoFocus && index === 0}
            onFocus={(e) => e.target.select()}
            onChange={(e) => write(index, e.target.value.replace(digit, '') || e.target.value)}
            onKeyDown={(e) => keyDown(index, e)}
            onPaste={(e) => paste(index, e)}
          />
        );
      })}
    </div>
  );
}

/** Escudo sobre el título de la verificación: anillo que respira, gira al comprobar y se vuelve palomita al aceptar. */
export function VerifyBadge({ state = 'idle' }: { state?: OtpState }) {
  return (
    <span className="verify-badge" data-state={state} aria-hidden="true">
      <span className="verify-ring" />
      {state === 'success' ? (
        <svg className="verify-check" width={30} height={30} viewBox="0 0 24 24" fill="none">
          <path
            d="M5 12.5l4.5 4.5L19 7.5"
            stroke="currentColor"
            strokeWidth="2.2"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      ) : (
        <Icon name="shield" size={28} />
      )}
    </span>
  );
}

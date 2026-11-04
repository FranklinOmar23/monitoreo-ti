import { useState, type FormEvent } from 'react';
import { useMutation } from '@tanstack/react-query';
import { ApiError } from '@monitoreoti/shared';
import { Modal } from '@components/Modal';
import { OTP_LENGTH, OtpInput, successPause, VerifyBadge, type OtpState } from '@components/OtpInput';
import { usePanelApi } from '../../data/PanelData';

/** El servidor pide confirmar el 2FA (su último código tiene más de 8 horas, decisión 52). */
export function needsTwoFactor(error: unknown): boolean {
  return error instanceof ApiError && error.codigo === '2fa_requerido';
}

interface TwoFactorPromptProps {
  onConfirmed: () => void;
  onCancel: () => void;
}

/** Pide el código de la app autenticadora sin cerrar la sesión; al confirmarlo se reintenta la acción. */
export function TwoFactorPrompt({ onConfirmed, onCancel }: TwoFactorPromptProps) {
  const api = usePanelApi();
  const [code, setCode] = useState('');
  const [attempt, setAttempt] = useState(0);
  const confirm = useMutation({
    mutationFn: (value: string) => api.remoto.confirmTwoFactor(value),
    onSuccess: () => setTimeout(onConfirmed, successPause()),
    onError: () => {
      setCode('');
      setAttempt((n) => n + 1);
    },
  });
  const digits = code.replace(/ /g, '');
  const state: OtpState = confirm.isPending
    ? 'checking'
    : confirm.isSuccess
      ? 'success'
      : confirm.isError && !digits
        ? 'error'
        : 'idle';

  const verify = (value: string) => {
    if (!confirm.isPending && !confirm.isSuccess) confirm.mutate(value);
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (digits.length === OTP_LENGTH) verify(digits);
  };

  return (
    <Modal
      title="Confirme su identidad"
      onClose={onCancel}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onCancel}>
            Cancelar
          </button>
          <button
            type="submit"
            form="confirm-2fa"
            className="btn btn-primary"
            disabled={state === 'checking' || state === 'success' || digits.length !== OTP_LENGTH}
          >
            {state === 'checking' ? 'Verificando…' : 'Confirmar'}
          </button>
        </>
      }
    >
      <form id="confirm-2fa" className="verify-prompt" onSubmit={submit}>
        <VerifyBadge state={state} />
        <p className="muted">Para controlar equipos se pide el código de su app autenticadora cada 8 horas.</p>
        {confirm.isError && (
          <p className="auth-error" role="alert">
            {confirm.error.message}
          </p>
        )}
        <OtpInput key={attempt} value={code} onChange={setCode} onComplete={verify} state={state} autoFocus />
        <p className="otp-status" role="status">
          {state === 'checking' && 'Verificando…'}
          {state === 'success' && 'Identidad confirmada'}
        </p>
      </form>
    </Modal>
  );
}

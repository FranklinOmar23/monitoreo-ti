import { useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { LANGUAGE_LABELS, type ScriptLanguage } from '@monitoreoti/shared';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../../data/PanelData';
import { needsTwoFactor, TwoFactorPrompt } from './TwoFactorPrompt';

export interface MachineRef {
  id: number;
  name: string;
}

interface RunScriptDialogProps {
  /** Equipos ya elegidos (desde la ficha o la selección de la lista); sin ellos se eligen aquí. */
  machines?: readonly MachineRef[];
  scriptId?: number;
  onClose: () => void;
  onStarted: (batchId: string) => void;
}

const LANGUAGES: ScriptLanguage[] = ['powershell', 'cmd', 'python'];

/** W-32 Ejecutar script: uno de la biblioteca o un comando escrito aquí, en uno o varios equipos. */
export function RunScriptDialog({ machines: preset, scriptId, onClose, onStarted }: RunScriptDialogProps) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const scripts = useQuery({ queryKey: ['scripts'], queryFn: () => api.remoto.scripts() });
  const [source, setSource] = useState(scriptId ? String(scriptId) : '');
  const [language, setLanguage] = useState<ScriptLanguage>('powershell');
  const [content, setContent] = useState('');
  const [timeout, setTimeoutS] = useState('300');
  const [chosen, setChosen] = useState<readonly MachineRef[]>(preset ?? []);
  const [askCode, setAskCode] = useState(false);

  const run = useMutation({
    mutationFn: () =>
      api.remoto.run({
        maquinaIds: chosen.map((m) => m.id),
        scriptId: source ? Number(source) : null,
        lenguaje: source ? null : language,
        contenido: source ? null : content,
        timeoutS: Number(timeout),
      }),
    onSuccess: ({ batchId }) => {
      void queries.invalidateQueries({ queryKey: ['ejecuciones'] });
      onStarted(batchId);
    },
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    run.mutate();
  };

  const selectedScript = scripts.data?.find((s) => String(s.id) === source);
  const title = preset?.length === 1 ? `Ejecutar script · ${preset[0]!.name}` : 'Ejecutar script';

  return (
    <>
      <Modal
        title={title}
        onClose={onClose}
        wide
        footer={
          <>
            <button type="button" className="btn btn-outline" onClick={onClose}>
              Cancelar
            </button>
            <button
              type="submit"
              form="run-script"
              className="btn btn-primary"
              disabled={run.isPending || chosen.length === 0 || (!source && !content.trim())}
            >
              {run.isPending
                ? 'Enviando…'
                : `Ejecutar en ${chosen.length} ${chosen.length === 1 ? 'equipo' : 'equipos'}`}
            </button>
          </>
        }
      >
        <form id="run-script" className="form-grid" onSubmit={submit}>
          {run.isError && !needsTwoFactor(run.error) && (
            <p className="auth-error form-span" role="alert">
              {run.error.message}
            </p>
          )}
          <label className="field form-span">
            <span>Qué ejecutar</span>
            <select value={source} onChange={(e) => setSource(e.target.value)}>
              <option value="">Escribir un comando</option>
              {(scripts.data ?? []).map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name} ({LANGUAGE_LABELS[s.language]})
                </option>
              ))}
            </select>
          </label>
          {selectedScript ? (
            <div className="form-span">
              {selectedScript.description && <p className="muted">{selectedScript.description}</p>}
              <pre className="code-block" aria-label="Contenido del script">
                {selectedScript.content}
              </pre>
            </div>
          ) : (
            <>
              <label className="field">
                <span>Lenguaje</span>
                <select value={language} onChange={(e) => setLanguage(e.target.value as ScriptLanguage)}>
                  {LANGUAGES.map((l) => (
                    <option key={l} value={l}>
                      {LANGUAGE_LABELS[l]}
                    </option>
                  ))}
                </select>
              </label>
              <label className="field form-span">
                <span>Comando</span>
                <textarea
                  className="code-input"
                  rows={6}
                  maxLength={100_000}
                  spellCheck={false}
                  value={content}
                  placeholder={language === 'cmd' ? 'ipconfig /all' : 'Get-Service Spooler | Restart-Service'}
                  onChange={(e) => setContent(e.target.value)}
                />
              </label>
            </>
          )}
          <label className="field">
            <span>Tiempo límite (segundos)</span>
            <input type="number" min={10} max={3600} value={timeout} onChange={(e) => setTimeoutS(e.target.value)} />
          </label>
          <div className="form-span">
            {preset && preset.length > 0 ? (
              <p className="muted">
                {preset.length === 1 ? 'Equipo: ' : `${preset.length} equipos: `}
                {preset.map((m) => m.name).join(', ')}
              </p>
            ) : (
              <MachinePicker chosen={chosen} onChange={setChosen} />
            )}
            <p className="muted">
              Se ejecuta con permisos del sistema. Un equipo apagado lo recibe al conectarse, hasta 24 horas.
            </p>
          </div>
        </form>
      </Modal>
      {askCode && (
        <TwoFactorPrompt
          onCancel={() => setAskCode(false)}
          onConfirmed={() => {
            setAskCode(false);
            run.mutate();
          }}
        />
      )}
    </>
  );
}

/** Buscar y marcar equipos (cuando el diálogo se abre desde la biblioteca). */
export function MachinePicker({
  chosen,
  onChange,
}: {
  chosen: readonly MachineRef[];
  onChange: (value: MachineRef[]) => void;
}) {
  const api = usePanelApi();
  const [q, setQ] = useState('');
  const list = useQuery({ queryKey: ['maquinas', 'selector', q], queryFn: () => api.machines({ q, tamano: 25 }) });
  const ids = new Set(chosen.map((m) => m.id));
  const toggle = (m: MachineRef) => onChange(ids.has(m.id) ? chosen.filter((c) => c.id !== m.id) : [...chosen, m]);

  return (
    <fieldset className="machine-picker">
      <legend>Equipos ({chosen.length})</legend>
      <input
        type="search"
        aria-label="Buscar equipos"
        placeholder="Buscar por nombre o IP"
        value={q}
        onChange={(e) => setQ(e.target.value)}
      />
      <ul>
        {chosen
          .filter((m) => !(list.data?.page.items ?? []).some((i) => i.id === m.id))
          .map((m) => (
            <li key={m.id}>
              <label>
                <input type="checkbox" checked onChange={() => toggle(m)} /> {m.name}
              </label>
            </li>
          ))}
        {(list.data?.page.items ?? []).map((m) => (
          <li key={m.id}>
            <label>
              <input type="checkbox" checked={ids.has(m.id)} onChange={() => toggle({ id: m.id, name: m.name })} />{' '}
              {m.name} <span className="muted">· {m.branchName}</span>
            </label>
          </li>
        ))}
      </ul>
    </fieldset>
  );
}

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  BackupDestinationType,
  BackupDisk,
  BackupFrequency,
  BackupJobInput,
  BackupJobItem,
  BackupMode,
  BackupSource,
  BackupStrategy,
} from '@monitoreoti/shared';
import { usePanelApi } from '../../data/PanelData';
import { FormModal } from '../red/NetworkForms';
import { needsTwoFactor, TwoFactorPrompt } from '../remoto/TwoFactorPrompt';

const WEEK = [
  { day: 1, label: 'Lun' },
  { day: 2, label: 'Mar' },
  { day: 3, label: 'Mié' },
  { day: 4, label: 'Jue' },
  { day: 5, label: 'Vie' },
  { day: 6, label: 'Sáb' },
  { day: 0, label: 'Dom' },
];

interface FormState {
  nombre: string;
  origen: BackupSource;
  instanciaSql: string;
  bases: string[];
  carpetas: string;
  modo: BackupMode;
  servicios: string[];
  frioMaxMinutos: number;
  estrategia: BackupStrategy;
  completoCadaDias: number;
  destinoTipo: BackupDestinationType;
  carpetaDisco: string;
  rutaRed: string;
  rutaLocal: string;
  discos: BackupDisk[];
  expulsar: boolean;
  redUsuario: string;
  redClave: string;
  frecuencia: BackupFrequency;
  diasSemana: number[];
  hora: string;
  retenerDiarios: number;
  retenerMensuales: number;
  verificar: boolean;
  activo: boolean;
}

function initial(job: BackupJobItem | null): FormState {
  return {
    nombre: job?.name ?? '',
    origen: job?.source ?? 'sql',
    instanciaSql: job?.sqlInstance ?? '',
    bases: job?.source === 'sql' ? job.items : [],
    carpetas: job?.source === 'carpetas' ? job.items.join('\n') : '',
    modo: job?.mode ?? 'caliente',
    servicios: job?.stopServices ?? [],
    frioMaxMinutos: job?.coldMaxMinutes ?? 30,
    estrategia: job?.strategy ?? 'incremental',
    completoCadaDias: job?.strategy === 'incremental' ? job.fullEveryDays : 7,
    destinoTipo: job?.destinationType ?? 'disco_externo',
    carpetaDisco: job?.destinationType === 'disco_externo' ? job.destinationPath : 'Centinela',
    rutaRed: job?.destinationType === 'red' ? job.destinationPath : '',
    rutaLocal: job?.destinationType === 'local' ? job.destinationPath : '',
    discos: job?.disks ?? [],
    expulsar: job?.ejectAfter ?? false,
    redUsuario: job?.networkUser ?? '',
    redClave: '',
    frecuencia: job?.frequency ?? 'diario',
    diasSemana: job?.weekDays ?? [1, 2, 3, 4, 5],
    hora: job?.time ?? '23:00',
    retenerDiarios: job?.keepDaily ?? 7,
    retenerMensuales: job?.keepMonthly ?? 3,
    verificar: job?.verify ?? true,
    activo: job?.isActive ?? true,
  };
}

const toggle = <T,>(list: T[], value: T) => (list.includes(value) ? list.filter((v) => v !== value) : [...list, value]);

/** Crear o editar un respaldo: qué, cómo, copias, dónde, cuándo y cuánto guardar (decisión 82). */
export function BackupJobDialog({
  machineId,
  job,
  onClose,
}: {
  machineId: number;
  job: BackupJobItem | null;
  onClose: () => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [form, setForm] = useState<FormState>(() => initial(job));
  const [serviceFilter, setServiceFilter] = useState('');
  const [askCode, setAskCode] = useState(false);
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setForm((f) => ({ ...f, [key]: value }));

  // Lo que hay en el servidor: instancias y bases, servicios y discos externos conectados.
  const explore = useQuery({
    queryKey: ['respaldos', machineId, 'explorar'],
    queryFn: () => api.respaldos.explore(machineId),
    staleTime: 5 * 60_000,
    retry: false,
  });
  const found = explore.data?.ok ? explore.data : null;
  const instances = found?.instances ?? [];
  const instance = form.instanciaSql || instances[0]?.name || '';
  const databases = instances.find((i) => i.name === instance);

  const destinationPath =
    form.destinoTipo === 'disco_externo' ? form.carpetaDisco : form.destinoTipo === 'red' ? form.rutaRed : form.rutaLocal;
  const test = useMutation({
    mutationFn: () =>
      api.respaldos.testDestination(machineId, {
        destinoTipo: form.destinoTipo,
        destinoRuta: destinationPath.trim() || null,
        discos: form.destinoTipo === 'disco_externo' ? form.discos : null,
        redUsuario: form.destinoTipo === 'red' && form.redUsuario.trim() ? form.redUsuario.trim() : null,
        redClave: form.redClave || null,
        trabajoId: job?.id ?? null,
      }),
  });

  const input = (): BackupJobInput => ({
    nombre: form.nombre.trim(),
    origen: form.origen,
    instanciaSql: form.origen === 'sql' ? instance : null,
    elementos:
      form.origen === 'sql'
        ? form.bases
        : form.carpetas
            .split(/\r?\n/)
            .map((l) => l.trim())
            .filter(Boolean),
    modo: form.modo,
    serviciosDetener: form.modo === 'frio' ? form.servicios : null,
    frioMaxMinutos: form.frioMaxMinutos,
    estrategia: form.estrategia,
    completoCadaDias: form.completoCadaDias,
    destinoTipo: form.destinoTipo,
    destinoRuta: destinationPath.trim() || null,
    discos: form.destinoTipo === 'disco_externo' ? form.discos : null,
    expulsar: form.expulsar,
    redUsuario: form.destinoTipo === 'red' && form.redUsuario.trim() ? form.redUsuario.trim() : null,
    redClave: form.redClave || null,
    frecuencia: form.frecuencia,
    diasSemana: form.frecuencia === 'semanal' ? form.diasSemana : null,
    hora: form.hora,
    retenerDiarios: form.retenerDiarios,
    retenerMensuales: form.retenerMensuales,
    verificar: form.verificar,
    activo: form.activo,
  });
  const save = useMutation({
    mutationFn: () => api.respaldos.save(machineId, job?.id ?? null, input()),
    onSuccess: () => {
      void queries.invalidateQueries({ queryKey: ['respaldos', machineId] });
      onClose();
    },
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });

  const connectedDisks = found?.disks ?? [];
  // Los elegidos que no están conectados ahora (el otro disco de la rotación) también se muestran.
  const diskChoices = [
    ...connectedDisks.map((d) => ({ serie: d.serial, nombre: d.name, info: `${d.letter ?? ''} ${d.totalGb ? `${d.totalGb} GB` : ''}`.trim() })),
    ...form.discos
      .filter((d) => !connectedDisks.some((c) => c.serial === d.serie))
      .map((d) => ({ ...d, info: 'no conectado ahora' })),
  ];
  const services = (found?.services ?? []).filter(
    (s) =>
      !serviceFilter ||
      s.displayName.toLowerCase().includes(serviceFilter.toLowerCase()) ||
      s.name.toLowerCase().includes(serviceFilter.toLowerCase()),
  );

  return (
    <>
      <FormModal
        id="backup-job-form"
        title={job ? `Editar «${job.name}»` : 'Nuevo respaldo'}
        wide
        pending={save.isPending}
        error={save.error && !needsTwoFactor(save.error) ? save.error : null}
        onClose={onClose}
        onSubmit={() => save.mutate()}
      >
        <label className="field form-span">
          <span>Nombre *</span>
          <input
            required
            maxLength={100}
            value={form.nombre}
            placeholder="Base del ERP a disco externo"
            onChange={(e) => set('nombre', e.target.value)}
          />
        </label>

        {explore.isPending && <p className="muted form-span">Leyendo el servidor (bases, servicios y discos)…</p>}
        {explore.isError && (
          <p className="api-test-result is-error form-span" role="alert">
            No se pudo leer el servidor: {explore.error.message}
          </p>
        )}
        {explore.data && !explore.data.ok && (
          <p className="api-test-result is-error form-span" role="alert">
            {explore.data.error ?? 'El agente no pudo leer el servidor.'}
          </p>
        )}

        <fieldset className="backup-section form-span">
          <legend>Qué respaldar</legend>
          <div className="backup-choice" role="radiogroup" aria-label="Qué respaldar">
            <label>
              <input type="radio" checked={form.origen === 'sql'} onChange={() => set('origen', 'sql')} />
              Bases de SQL Server
            </label>
            <label>
              <input type="radio" checked={form.origen === 'carpetas'} onChange={() => set('origen', 'carpetas')} />
              Carpetas
            </label>
          </div>
          {form.origen === 'sql' ? (
            <>
              {instances.length > 1 && (
                <label className="field">
                  <span>Instancia</span>
                  <select value={instance} onChange={(e) => setForm((f) => ({ ...f, instanciaSql: e.target.value, bases: [] }))}>
                    {instances.map((i) => (
                      <option key={i.name} value={i.name}>
                        {i.name}
                      </option>
                    ))}
                  </select>
                </label>
              )}
              {found && instances.length === 0 && <p className="muted">No se encontró SQL Server en este servidor.</p>}
              {databases?.error && (
                <p className="api-test-result is-error" role="alert">
                  {databases.error}
                </p>
              )}
              {databases && databases.databases.length > 0 && (
                <ul className="backup-checklist" aria-label="Bases de datos">
                  {databases.databases.map((d) => (
                    <li key={d.name}>
                      <label>
                        <input
                          type="checkbox"
                          checked={form.bases.includes(d.name)}
                          onChange={() => set('bases', toggle(form.bases, d.name))}
                        />
                        <span className="mono">{d.name}</span>
                        <span className="muted">
                          {d.sizeMb >= 1024 ? `${(d.sizeMb / 1024).toFixed(1)} GB` : `${d.sizeMb} MB`}
                        </span>
                      </label>
                    </li>
                  ))}
                </ul>
              )}
              {!found && (
                <label className="field">
                  <span>Bases (separadas por coma)</span>
                  <input
                    className="mono"
                    value={form.bases.join(', ')}
                    onChange={(e) =>
                      set(
                        'bases',
                        e.target.value
                          .split(',')
                          .map((s) => s.trim())
                          .filter(Boolean),
                      )
                    }
                  />
                </label>
              )}
              <p className="muted backup-help">
                El agente entra como SYSTEM: en SQL Server, dé al login <span className="mono">NT AUTHORITY\SYSTEM</span> el
                rol <span className="mono">db_backupoperator</span> en estas bases.
              </p>
            </>
          ) : (
            <label className="field">
              <span>Carpetas del servidor (una por línea)</span>
              <textarea
                className="mono"
                rows={3}
                value={form.carpetas}
                placeholder={'D:\\Datos\nE:\\Documentos'}
                onChange={(e) => set('carpetas', e.target.value)}
              />
            </label>
          )}
        </fieldset>

        <fieldset className="backup-section form-span">
          <legend>Cómo</legend>
          <div className="backup-choice" role="radiogroup" aria-label="Cómo">
            <label>
              <input type="radio" checked={form.modo === 'caliente'} onChange={() => set('modo', 'caliente')} />
              En caliente (sin detener nada)
            </label>
            <label>
              <input type="radio" checked={form.modo === 'frio'} onChange={() => set('modo', 'frio')} />
              En frío (detiene servicios)
            </label>
          </div>
          {form.modo === 'frio' && (
            <>
              <p className="notice">
                Mientras dure el respaldo, esos servicios no estarán disponibles. Siempre se vuelven a iniciar, aunque el
                respaldo falle; en carpetas, apenas se toma la instantánea, y en SQL Server, apenas termina la copia de la
                base.
              </p>
              {form.servicios.length > 0 && (
                <p className="backup-chips">
                  {form.servicios.map((s) => (
                    <button key={s} type="button" className="tag-soft" onClick={() => set('servicios', toggle(form.servicios, s))}>
                      {s} ×
                    </button>
                  ))}
                </p>
              )}
              {found ? (
                <>
                  <input
                    type="search"
                    aria-label="Buscar servicio"
                    placeholder="Buscar servicio…"
                    value={serviceFilter}
                    onChange={(e) => setServiceFilter(e.target.value)}
                  />
                  <ul className="backup-checklist backup-services" aria-label="Servicios">
                    {services.slice(0, 200).map((s) => (
                      <li key={s.name}>
                        <label>
                          <input
                            type="checkbox"
                            checked={form.servicios.includes(s.name)}
                            onChange={() => set('servicios', toggle(form.servicios, s.name))}
                          />
                          <span>{s.displayName}</span>
                          <span className="muted mono">{s.name}</span>
                        </label>
                      </li>
                    ))}
                  </ul>
                </>
              ) : (
                <label className="field">
                  <span>Servicios (nombre corto, separados por coma)</span>
                  <input
                    className="mono"
                    value={form.servicios.join(', ')}
                    onChange={(e) =>
                      set(
                        'servicios',
                        e.target.value
                          .split(',')
                          .map((s) => s.trim())
                          .filter(Boolean),
                      )
                    }
                  />
                </label>
              )}
              <label className="field backup-inline">
                <span>Tiempo máximo sin servicio (minutos)</span>
                <input
                  type="number"
                  min={5}
                  max={720}
                  value={form.frioMaxMinutos}
                  onChange={(e) => set('frioMaxMinutos', Number(e.target.value))}
                />
              </label>
            </>
          )}
        </fieldset>

        <fieldset className="backup-section form-span">
          <legend>Copias</legend>
          <div className="backup-choice" role="radiogroup" aria-label="Copias">
            <label>
              <input type="radio" checked={form.estrategia === 'incremental'} onChange={() => set('estrategia', 'incremental')} />
              Solo lo nuevo
            </label>
            <label>
              <input type="radio" checked={form.estrategia === 'completo'} onChange={() => set('estrategia', 'completo')} />
              Completo cada vez
            </label>
          </div>
          {form.estrategia === 'incremental' ? (
            <>
              <p className="muted backup-help">
                La primera vez copia todo; después solo lo nuevo o cambiado (en SQL Server, un diferencial). Cada cierto
                tiempo hace otra copia completa para que la cadena no crezca sin fin.
              </p>
              <label className="field backup-inline">
                <span>Copia completa cada (días)</span>
                <input
                  type="number"
                  min={1}
                  max={60}
                  value={form.completoCadaDias}
                  onChange={(e) => set('completoCadaDias', Number(e.target.value))}
                />
              </label>
            </>
          ) : (
            <p className="muted backup-help">Cada respaldo es una copia completa que sirve sola para restaurar.</p>
          )}
        </fieldset>

        <fieldset className="backup-section form-span">
          <legend>Dónde guardarlo</legend>
          <div className="backup-choice" role="radiogroup" aria-label="Dónde guardarlo">
            <label>
              <input type="radio" checked={form.destinoTipo === 'disco_externo'} onChange={() => set('destinoTipo', 'disco_externo')} />
              Disco externo
            </label>
            <label>
              <input type="radio" checked={form.destinoTipo === 'red'} onChange={() => set('destinoTipo', 'red')} />
              NAS o carpeta de red
            </label>
            <label>
              <input type="radio" checked={form.destinoTipo === 'local'} onChange={() => set('destinoTipo', 'local')} />
              Disco del servidor
            </label>
          </div>

          {form.destinoTipo === 'disco_externo' && (
            <>
              <p className="muted backup-help">
                El disco se reconoce por su número de serie, no por la letra. Elija dos para rotarlos: se usa el que esté
                conectado. Una hora antes, si no hay ninguno, llega un aviso.
              </p>
              {diskChoices.length === 0 ? (
                <p className="muted">{found ? 'Conecte el disco externo al servidor y vuelva a abrir este formulario.' : ''}</p>
              ) : (
                <ul className="backup-checklist" aria-label="Discos externos">
                  {diskChoices.map((d) => (
                    <li key={d.serie}>
                      <label>
                        <input
                          type="checkbox"
                          checked={form.discos.some((x) => x.serie === d.serie)}
                          onChange={() =>
                            set(
                              'discos',
                              form.discos.some((x) => x.serie === d.serie)
                                ? form.discos.filter((x) => x.serie !== d.serie)
                                : [...form.discos, { serie: d.serie, nombre: d.nombre }],
                            )
                          }
                        />
                        <span>{d.nombre}</span>
                        <span className="muted mono">{d.info}</span>
                      </label>
                    </li>
                  ))}
                </ul>
              )}
              <label className="field backup-inline">
                <span>Carpeta dentro del disco</span>
                <input className="mono" maxLength={120} value={form.carpetaDisco} onChange={(e) => set('carpetaDisco', e.target.value)} />
              </label>
              <label className="check">
                <input type="checkbox" checked={form.expulsar} onChange={(e) => set('expulsar', e.target.checked)} />
                Expulsar el disco al terminar (para llevárselo)
              </label>
            </>
          )}

          {form.destinoTipo === 'red' && (
            <div className="backup-grid">
              <label className="field form-span">
                <span>Carpeta de red *</span>
                <input
                  className="mono"
                  value={form.rutaRed}
                  placeholder={'\\\\nas\\respaldos'}
                  onChange={(e) => set('rutaRed', e.target.value)}
                />
              </label>
              <label className="field">
                <span>Cuenta (solo para respaldos)</span>
                <input
                  autoComplete="off"
                  maxLength={150}
                  value={form.redUsuario}
                  placeholder="NAS\respaldos"
                  onChange={(e) => set('redUsuario', e.target.value)}
                />
              </label>
              <label className="field">
                <span>Clave</span>
                <input
                  type="password"
                  autoComplete="new-password"
                  maxLength={256}
                  value={form.redClave}
                  placeholder={job?.hasNetworkPassword ? 'Guardada; escriba para cambiarla' : ''}
                  onChange={(e) => set('redClave', e.target.value)}
                />
              </label>
              <p className="muted backup-help form-span">
                La clave se guarda cifrada y solo la abre el agente de este servidor; nunca se muestra.
              </p>
            </div>
          )}

          {form.destinoTipo === 'local' && (
            <label className="field">
              <span>Carpeta *</span>
              <input
                className="mono"
                value={form.rutaLocal}
                placeholder="E:\Respaldos"
                onChange={(e) => set('rutaLocal', e.target.value)}
              />
            </label>
          )}

          <div className="api-test">
            <button type="button" className="btn btn-outline" disabled={test.isPending} onClick={() => test.mutate()}>
              {test.isPending ? 'Probando…' : 'Probar destino'}
            </button>
            {test.isError && <span className="api-test-result is-error">{test.error.message}</span>}
            {test.data && (
              <span className={`api-test-result ${test.data.ok ? 'is-ok' : 'is-error'}`} role="status">
                {test.data.ok
                  ? `Listo: ${test.data.location ?? 'se pudo escribir'}${test.data.freeGb !== null ? ` · ${test.data.freeGb} GB libres` : ''}`
                  : (test.data.error ?? 'No se pudo usar el destino.')}
              </span>
            )}
          </div>
        </fieldset>

        <fieldset className="backup-section form-span">
          <legend>Cuándo y cuánto guardar</legend>
          <div className="backup-choice" role="radiogroup" aria-label="Frecuencia">
            <label>
              <input type="radio" checked={form.frecuencia === 'diario'} onChange={() => set('frecuencia', 'diario')} />
              Todos los días
            </label>
            <label>
              <input type="radio" checked={form.frecuencia === 'semanal'} onChange={() => set('frecuencia', 'semanal')} />
              Algunos días
            </label>
          </div>
          {form.frecuencia === 'semanal' && (
            <p className="backup-days" role="group" aria-label="Días">
              {WEEK.map((w) => (
                <label key={w.day} className="check">
                  <input
                    type="checkbox"
                    checked={form.diasSemana.includes(w.day)}
                    onChange={() => set('diasSemana', toggle(form.diasSemana, w.day))}
                  />
                  {w.label}
                </label>
              ))}
            </p>
          )}
          <div className="backup-grid">
            <label className="field">
              <span>Hora (del servidor)</span>
              <input type="time" required value={form.hora} onChange={(e) => set('hora', e.target.value)} />
            </label>
            <label className="field">
              <span>Respaldos diarios que se guardan</span>
              <input
                type="number"
                min={1}
                max={90}
                value={form.retenerDiarios}
                onChange={(e) => set('retenerDiarios', Number(e.target.value))}
              />
            </label>
            <label className="field">
              <span>Mensuales que se guardan</span>
              <input
                type="number"
                min={0}
                max={36}
                value={form.retenerMensuales}
                onChange={(e) => set('retenerMensuales', Number(e.target.value))}
              />
            </label>
          </div>
          <p className="muted backup-help">
            Lo viejo se borra solo, pero nunca una copia completa de la que dependa algún respaldo que se guarda.
          </p>
          <label className="check">
            <input type="checkbox" checked={form.verificar} onChange={(e) => set('verificar', e.target.checked)} />
            Comprobar la copia al terminar
          </label>
          <label className="check">
            <input type="checkbox" checked={form.activo} onChange={(e) => set('activo', e.target.checked)} />
            Activo (desmárquelo para pausarlo)
          </label>
        </fieldset>
      </FormModal>
      {askCode && (
        <TwoFactorPrompt
          onConfirmed={() => {
            setAskCode(false);
            save.mutate();
          }}
          onCancel={() => setAskCode(false)}
        />
      )}
    </>
  );
}

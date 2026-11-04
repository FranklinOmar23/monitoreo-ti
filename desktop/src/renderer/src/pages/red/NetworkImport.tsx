import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { IMPORT_COLUMNS, type ImportResult, type ImportType } from '@monitoreoti/shared';
import { Icon } from '@components/Icon';
import { usePanelApi } from '../../data/PanelData';

const TYPES: { id: ImportType; label: string; help: string; example: string }[] = [
  {
    id: 'dispositivos',
    label: 'Dispositivos',
    help: 'El modelo debe estar en el catálogo. La sala solo hace falta si hay dos gabinetes con el mismo nombre en la sucursal.',
    example: 'SW-ACC-05,Cisco,Catalyst 9200-48P,Santo Domingo,Data Center Principal,GAB-A01,30,10.10.0.5,17.9.4',
  },
  {
    id: 'rosetas',
    label: 'Rosetas',
    help: 'Un código por roseta, único en su sucursal.',
    example: 'P-2-16,"Contabilidad, escritorio 4",Santo Domingo',
  },
  {
    id: 'cables',
    label: 'Cables',
    help: 'Cada cable va de un puerto a otro puerto (dispositivo_b y puerto_b) o a una roseta. El puerto se indica por su nombre (Gi1/0/12, P12) o su número.',
    example: 'PP-A01-01,P16,,,P-2-16,cat6,azul,P-2-16,',
  },
];

/** Lee el CSV como UTF-8; si no lo es, como Windows-1252 (lo que guarda Excel con "CSV delimitado por comas"). */
export async function readCsv(file: Blob): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return new TextDecoder('windows-1252').decode(bytes);
  }
}

/** W-68 Importación masiva: plantilla, vista previa con errores por fila e importación de todo o nada. */
export function NetworkImport() {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [type, setType] = useState<ImportType>('dispositivos');
  const [file, setFile] = useState<{ name: string; content: string } | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [templateError, setTemplateError] = useState<string | null>(null);
  const current = TYPES.find((t) => t.id === type)!;

  const preview = useMutation({
    mutationFn: (content: string) => api.red.previewImport(type, content),
    onSuccess: setResult,
  });
  const run = useMutation({
    mutationFn: (content: string) => api.red.runImport(type, content),
    onSuccess: (r) => {
      setResult(r);
      if (r.applied) void queries.invalidateQueries({ queryKey: ['red'] });
    },
  });

  const reset = (next: ImportType) => {
    setType(next);
    setFile(null);
    setResult(null);
    preview.reset();
    run.reset();
  };

  const choose = async (picked: File | undefined) => {
    if (!picked) return;
    const content = await readCsv(picked);
    setFile({ name: picked.name, content });
    setResult(null);
    run.reset();
    preview.mutate(content);
  };

  const downloadTemplate = async () => {
    setTemplateError(null);
    const header = IMPORT_COLUMNS[type].map((c) => c.replace('*', '')).join(',');
    // Con BOM, Excel abre las tildes bien.
    const data = new TextEncoder().encode(`\uFEFF${header}\r\n${current.example}\r\n`);
    try {
      if (!window.monitoreo) throw new Error('La descarga solo funciona dentro del panel.');
      const saved = await window.monitoreo.saveFile(`plantilla-${type}.csv`, data);
      if (saved.error) setTemplateError(saved.error);
    } catch (error) {
      setTemplateError(error instanceof Error ? error.message : 'No se pudo guardar la plantilla.');
    }
  };

  const error = preview.error ?? run.error;
  const busy = preview.isPending || run.isPending;
  const withErrors = result ? result.total - result.valid : 0;

  return (
    <div className="import-page">
      <div className="segmented segmented-lg" role="group" aria-label="Qué importar">
        {TYPES.map((t) => (
          <button key={t.id} type="button" aria-pressed={type === t.id} onClick={() => reset(t.id)}>
            {t.label}
          </button>
        ))}
      </div>

      <div className="card import-help">
        <p>{current.help}</p>
        <p className="mono import-columns">
          {IMPORT_COLUMNS[type].map((c) => (
            <span key={c} className={c.endsWith('*') ? 'is-required' : undefined}>
              {c.replace('*', '')}
            </span>
          ))}
        </p>
        <p className="muted">
          Las columnas resaltadas son obligatorias. Sirve un CSV separado por comas o por punto y coma (en Excel:
          Guardar como → CSV). Las claves SNMP no se importan: se escriben en cada dispositivo.
        </p>
        <div className="row-actions">
          <button type="button" className="btn btn-outline" onClick={() => void downloadTemplate()}>
            <Icon name="download" size={16} /> Descargar plantilla
          </button>
          <label className="btn btn-primary">
            <Icon name="upload" size={16} /> {file ? 'Elegir otro archivo' : 'Elegir archivo CSV'}
            <input
              type="file"
              accept=".csv,.txt,text/csv"
              className="visually-hidden"
              aria-label="Archivo CSV"
              onChange={(e) => {
                void choose(e.target.files?.[0]);
                e.target.value = '';
              }}
            />
          </label>
        </div>
        {templateError && (
          <p className="auth-error" role="alert">
            {templateError}
          </p>
        )}
      </div>

      {error && (
        <p className="auth-error" role="alert">
          {error.message}
        </p>
      )}
      {busy && <p className="muted">{run.isPending ? 'Importando…' : 'Revisando el archivo…'}</p>}

      {result && file && !busy && (
        <div className="card">
          {result.fileErrors.length > 0 ? (
            <div role="alert" className="import-file-errors">
              <strong>No se puede leer {file.name}</strong>
              {result.fileErrors.map((e) => (
                <span key={e}>{e}</span>
              ))}
            </div>
          ) : (
            <>
              <div className="import-summary" role="status">
                {result.applied ? (
                  <strong className="is-ok">
                    Se importaron {result.total} {result.total === 1 ? 'fila' : 'filas'} de {file.name}.
                  </strong>
                ) : (
                  <>
                    <strong>
                      {file.name}: {result.total} {result.total === 1 ? 'fila' : 'filas'}, {result.valid}{' '}
                      {result.valid === 1 ? 'lista' : 'listas'}
                      {withErrors > 0 && `, ${withErrors} con errores`}
                    </strong>
                    {withErrors > 0 && (
                      <span className="muted">Corrija el archivo y vuelva a elegirlo: se guarda todo o nada.</span>
                    )}
                  </>
                )}
                {!result.applied && withErrors === 0 && (
                  <button type="button" className="btn btn-primary" onClick={() => run.mutate(file.content)}>
                    Importar {result.total} {result.total === 1 ? 'fila' : 'filas'}
                  </button>
                )}
              </div>
              <table className="data-table import-rows">
                <thead>
                  <tr>
                    <th>Línea</th>
                    <th>Fila</th>
                    <th>Resultado</th>
                  </tr>
                </thead>
                <tbody>
                  {result.rows.map((row) => (
                    <tr key={row.line} className={row.errors.length > 0 ? 'has-errors' : undefined}>
                      <td className="mono">{row.line}</td>
                      <td className="mono">{row.summary}</td>
                      <td>
                        {row.errors.length === 0 ? (
                          <span className="is-ok">{result.applied ? 'Importada' : 'Lista'}</span>
                        ) : (
                          row.errors.map((e) => (
                            <span key={e} className="row-error">
                              {e}
                            </span>
                          ))
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </div>
      )}
    </div>
  );
}

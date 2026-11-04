import { useCallback, useEffect, useMemo, useState } from 'react';
import { Icon } from '@components/Icon';
import type { FileBridge } from '../../../../preload/bridge';
import { dateTime } from '../../data/format';
import {
  FileClient,
  formatBytes,
  joinPath,
  parentPath,
  pathSegments,
  safeLocalName,
  type Listing,
  type RemoteEntry,
} from './fileClient';

interface Transfer {
  key: number;
  kind: 'descarga' | 'subida';
  name: string;
  done: number;
  total: number;
  status: 'curso' | 'listo' | 'error' | 'cancelado';
  message?: string;
  cancel?: () => void;
}

interface FileExplorerProps {
  /** Socket del relay ya conectado con el equipo. */
  socket: WebSocket;
  /** Disco local por el proceso principal; sin él (pruebas) no se puede descargar ni subir. */
  files?: FileBridge;
}

let transferKey = 1;

/** W-31 Explorador de archivos: unidades y carpetas del equipo, descargar, subir, crear, renombrar y eliminar. */
export function FileExplorer({ socket, files = window.monitoreo?.files }: FileExplorerProps) {
  const client = useMemo(() => new FileClient(socket), [socket]);
  const [path, setPath] = useState('');
  const [listing, setListing] = useState<Listing | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [transfers, setTransfers] = useState<Transfer[]>([]);

  useEffect(() => () => client.dispose(), [client]);

  const open = useCallback(
    (target: string) => {
      setLoading(true);
      setError(null);
      client
        .list(target)
        .then((result) => {
          setListing(result);
          setPath(result.ruta);
        })
        .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
        .finally(() => setLoading(false));
    },
    [client],
  );

  useEffect(() => {
    let cancelled = false;
    client
      .list('')
      .then((result) => {
        if (!cancelled) setListing(result);
      })
      .catch((e: unknown) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [client]);

  const update = (key: number, patch: Partial<Transfer>) =>
    setTransfers((list) => list.map((t) => (t.key === key ? { ...t, ...patch } : t)));

  const act = (task: () => Promise<unknown>) =>
    task()
      .then(() => open(path))
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));

  const download = async (entry: RemoteEntry) => {
    if (!files) return;
    const target = await files.beginDownload(safeLocalName(entry.nombre));
    if (!target) return;
    const key = transferKey++;
    const job = client.download(
      joinPath(path, entry.nombre),
      {
        write: (data) => files.write(target.id, data),
        end: async (ok) => {
          await files.endDownload(target.id, ok);
        },
      },
      (done, total) => update(key, { done, total }),
    );
    setTransfers((list) => [
      {
        key,
        kind: 'descarga',
        name: entry.nombre,
        done: 0,
        total: entry.tamano ?? 0,
        status: 'curso',
        cancel: job.cancel,
      },
      ...list,
    ]);
    job.done.then(
      () => update(key, { status: 'listo', cancel: undefined }),
      (e: Error) =>
        update(key, {
          status: e.message.includes('cancelada') ? 'cancelado' : 'error',
          message: e.message,
          cancel: undefined,
        }),
    );
  };

  const upload = async (replace = false, picked?: { id: number; name: string; size: number }) => {
    if (!files || !path) return;
    const source = picked ?? (await files.pickUpload());
    if (!source) return;
    const key = transferKey++;
    const job = client.upload(
      joinPath(path, source.name),
      { size: source.size, read: (offset, length) => files.read(source.id, offset, length) },
      replace,
      (done, total) => update(key, { done, total }),
    );
    setTransfers((list) => [
      { key, kind: 'subida', name: source.name, done: 0, total: source.size, status: 'curso', cancel: job.cancel },
      ...list.filter((t) => t.key !== key),
    ]);
    try {
      await job.done;
      update(key, { status: 'listo', cancel: undefined });
      await files.endUpload(source.id);
      open(path);
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      if (
        !replace &&
        message.includes('Ya existe') &&
        window.confirm(`${source.name} ya existe en esta carpeta. ¿Reemplazarlo?`)
      ) {
        setTransfers((list) => list.filter((t) => t.key !== key));
        return upload(true, source);
      }
      update(key, { status: message.includes('cancelada') ? 'cancelado' : 'error', message, cancel: undefined });
      await files.endUpload(source.id);
    }
  };

  const createFolder = () => {
    const name = window.prompt('Nombre de la carpeta nueva');
    if (name?.trim()) void act(() => client.createFolder(joinPath(path, name.trim())));
  };

  const rename = (entry: RemoteEntry) => {
    const name = window.prompt('Nombre nuevo', entry.nombre);
    if (name?.trim() && name.trim() !== entry.nombre)
      void act(() => client.rename(joinPath(path, entry.nombre), name.trim()));
  };

  const remove = (entry: RemoteEntry) => {
    const what = entry.tipo === 'carpeta' ? `la carpeta ${entry.nombre} y todo su contenido` : entry.nombre;
    if (window.confirm(`¿Eliminar ${what} del equipo? No se puede deshacer.`))
      void act(() => client.remove(joinPath(path, entry.nombre)));
  };

  const entries = [...(listing?.entradas ?? [])].sort(
    (a, b) => Number(b.tipo !== 'archivo') - Number(a.tipo !== 'archivo') || a.nombre.localeCompare(b.nombre, 'es'),
  );
  const segments = pathSegments(path);

  return (
    <div className="file-explorer">
      <div className="file-toolbar">
        <nav className="file-path" aria-label="Ubicación">
          <button type="button" className="link-quiet" onClick={() => open('')}>
            Este equipo
          </button>
          {segments.map((segment, i) => (
            <span key={i}>
              <span aria-hidden="true"> › </span>
              <button
                type="button"
                className="link-quiet"
                onClick={() => open(i === 0 ? segment : `${segments[0]}${segments.slice(1, i + 1).join('\\')}`)}
              >
                {segment}
              </button>
            </span>
          ))}
        </nav>
        <div className="file-actions">
          <button type="button" className="btn btn-outline" disabled={!path} onClick={() => open(parentPath(path))}>
            Subir un nivel
          </button>
          <button type="button" className="btn btn-outline" disabled={!path} onClick={createFolder}>
            <Icon name="folder" size={14} /> Nueva carpeta
          </button>
          <button type="button" className="btn btn-primary" disabled={!path || !files} onClick={() => void upload()}>
            <Icon name="upload" size={14} /> Subir archivo
          </button>
        </div>
      </div>

      {error && (
        <p className="auth-error" role="alert">
          {error}
        </p>
      )}
      {listing?.recortado && <p className="muted">Se muestran los primeros 5000 elementos de esta carpeta.</p>}

      <div className="table-card file-table" aria-busy={loading}>
        <table className="data-table">
          <thead>
            <tr>
              <th>Nombre</th>
              <th>Tamaño</th>
              <th>Modificado</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {entries.map((entry) => (
              <tr key={entry.nombre}>
                <td>
                  {entry.tipo === 'archivo' ? (
                    <span>{entry.nombre}</span>
                  ) : (
                    <button
                      type="button"
                      className="link-quiet"
                      onClick={() => open(path ? joinPath(path, entry.nombre) : entry.nombre)}
                    >
                      <Icon name={entry.tipo === 'unidad' ? 'disk' : 'folder'} size={14} /> {entry.nombre}
                    </button>
                  )}
                </td>
                <td className="mono">{formatBytes(entry.tamano)}</td>
                <td>{entry.modificado ? dateTime(entry.modificado) : ''}</td>
                <td className="file-row-actions">
                  {entry.tipo === 'archivo' && files && (
                    <button type="button" className="link-quiet" onClick={() => void download(entry)}>
                      Descargar
                    </button>
                  )}
                  {entry.tipo !== 'unidad' && (
                    <>
                      <button type="button" className="link-quiet" onClick={() => rename(entry)}>
                        Renombrar
                      </button>
                      <button type="button" className="link-quiet" onClick={() => remove(entry)}>
                        Eliminar
                      </button>
                    </>
                  )}
                </td>
              </tr>
            ))}
            {!loading && entries.length === 0 && (
              <tr>
                <td colSpan={4} className="muted">
                  Carpeta vacía.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      {transfers.length > 0 && (
        <section className="transfers" aria-label="Transferencias">
          {transfers.map((t) => (
            <div key={t.key} className="transfer" data-status={t.status}>
              <span>
                {t.kind === 'descarga' ? '↓' : '↑'} {t.name}
              </span>
              <progress max={t.total || 1} value={t.status === 'listo' ? t.total || 1 : t.done} />
              <span className="muted">
                {t.status === 'curso' && `${formatBytes(t.done)} de ${formatBytes(t.total)}`}
                {t.status === 'listo' && 'Listo'}
                {t.status === 'cancelado' && 'Cancelado'}
                {t.status === 'error' && t.message}
              </span>
              {t.cancel && (
                <button type="button" className="link-quiet" onClick={t.cancel}>
                  Cancelar
                </button>
              )}
            </div>
          ))}
        </section>
      )}
    </div>
  );
}

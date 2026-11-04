import { useCallback, useEffect, useMemo, useState, type DragEvent } from 'react';
import { Icon } from '@components/Icon';
import { dateTime } from '../../data/format';
import {
  FileClient,
  formatBytes,
  joinPath,
  pathSegments,
  safeLocalName,
  type Listing,
  type RemoteEntry,
  type UploadSource,
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
  startedAt: number;
  /** Bytes por segundo desde que empezó. */
  rate: number;
}

interface FilePanelProps {
  socket: WebSocket;
  machineName: string;
  onClose: () => void;
}

/** Marcador de las partes de archivo dentro del escritorio (las de pantalla empiezan con 1, JPEG, o 3, video). */
const FILE_PART = 2;
let transferKey = 1;

/** Un archivo que se soltó en el panel, leído por partes desde el propio visor. */
function droppedSource(file: File): UploadSource {
  return {
    size: file.size,
    read: async (offset, length) => new Uint8Array(await file.slice(offset, offset + length).arrayBuffer()),
  };
}

/**
 * "Transferir archivos" dentro del escritorio remoto (W-28e): carpetas del equipo, descargar a esta PC y subir
 * arrastrando archivos al panel. Va por la misma sesión del escritorio (sus partes llevan el marcador 2).
 */
export function FilePanel({ socket, machineName, onClose }: FilePanelProps) {
  const client = useMemo(() => new FileClient(socket, FILE_PART), [socket]);
  const files = window.monitoreo?.files;
  const [listing, setListing] = useState<Listing | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const [dragging, setDragging] = useState(false);

  useEffect(() => () => client.dispose(), [client]);

  const open = useCallback(
    (path: string) => {
      setError(null);
      client
        .list(path)
        .then(setListing)
        .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
    },
    [client],
  );

  useEffect(() => {
    let cancelled = false;
    client
      .list('')
      .then((result) => !cancelled && setListing(result))
      .catch((e: unknown) => !cancelled && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
    };
  }, [client]);

  const path = listing?.ruta ?? '';
  const update = (key: number, patch: Partial<Transfer>) =>
    setTransfers((list) =>
      list.map((t) =>
        t.key === key
          ? {
              ...t,
              ...patch,
              rate: patch.done === undefined ? t.rate : patch.done / Math.max(1, (Date.now() - t.startedAt) / 1000),
            }
          : t,
      ),
    );
  const finish = (key: number, job: Promise<void>) =>
    job.then(
      () => update(key, { status: 'listo', cancel: undefined }),
      (e: Error) =>
        update(key, {
          status: e.message.includes('cancelada') ? 'cancelado' : 'error',
          message: e.message,
          cancel: undefined,
        }),
    );

  const download = async (entry: RemoteEntry) => {
    if (!files) return;
    const target = await files.beginDownload(safeLocalName(entry.nombre));
    if (!target) return;
    const key = transferKey++;
    const job = client.download(
      joinPath(path, entry.nombre),
      {
        write: (data) => files.write(target.id, data),
        end: async (ok) => void (await files.endDownload(target.id, ok)),
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
        startedAt: Date.now(),
        rate: 0,
      },
      ...list,
    ]);
    void finish(key, job.done);
  };

  const upload = (name: string, source: UploadSource, replace = false) => {
    if (!path) return;
    const key = transferKey++;
    const job = client.upload(joinPath(path, name), source, replace, (done, total) => update(key, { done, total }));
    setTransfers((list) => [
      {
        key,
        kind: 'subida',
        name,
        done: 0,
        total: source.size,
        status: 'curso',
        cancel: job.cancel,
        startedAt: Date.now(),
        rate: 0,
      },
      ...list,
    ]);
    job.done.then(
      () => {
        update(key, { status: 'listo', cancel: undefined });
        open(path);
      },
      (e: Error) => {
        if (
          !replace &&
          e.message.includes('Ya existe') &&
          window.confirm(`${name} ya existe en esta carpeta. ¿Reemplazarlo?`)
        ) {
          setTransfers((list) => list.filter((t) => t.key !== key));
          upload(name, source, true);
          return;
        }
        update(key, {
          status: e.message.includes('cancelada') ? 'cancelado' : 'error',
          message: e.message,
          cancel: undefined,
        });
      },
    );
  };

  const onDrop = (event: DragEvent) => {
    event.preventDefault();
    setDragging(false);
    if (!path) {
      setError('Abra primero la carpeta del equipo donde quiere guardar los archivos.');
      return;
    }
    for (const file of Array.from(event.dataTransfer.files)) upload(file.name, droppedSource(file));
  };

  const segments = pathSegments(path);
  const entries = [...(listing?.entradas ?? [])].sort(
    (a, b) => Number(b.tipo !== 'archivo') - Number(a.tipo !== 'archivo') || a.nombre.localeCompare(b.nombre, 'es'),
  );

  return (
    <aside className="file-panel" aria-label="Transferir archivos">
      <header className="file-panel-head">
        <strong>
          <Icon name="transfer" size={16} /> Transferir archivos
        </strong>
        <button type="button" className="icon-button" aria-label="Cerrar archivos" onClick={onClose}>
          <Icon name="close" size={16} />
        </button>
      </header>

      <nav className="file-panel-path" aria-label="Ubicación">
        <button type="button" className="link-quiet" onClick={() => open('')}>
          <Icon name="disk" size={13} /> Equipo
        </button>
        {segments.map((segment, i) => (
          <span key={i}>
            <span aria-hidden="true"> › </span>
            <button
              type="button"
              className="link-quiet"
              onClick={() => open(i === 0 ? segment : `${segments[0]}${segments.slice(1, i + 1).join('\\')}`)}
            >
              {segment.replace('\\', '')}
            </button>
          </span>
        ))}
      </nav>

      <div
        className="file-drop"
        data-active={dragging}
        data-disabled={!path}
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
      >
        <Icon name="upload" size={18} />
        <strong>Arrastra archivos aquí para enviarlos</strong>
        <span>{path ? `Se guardan en esta carpeta de ${machineName}` : 'Abra una carpeta del equipo primero'}</span>
      </div>

      {error && (
        <p className="auth-error" role="alert">
          {error}
        </p>
      )}

      <ul className="file-panel-list">
        {entries.map((entry) => (
          <li key={entry.nombre}>
            {entry.tipo === 'archivo' ? (
              <span className="file-row">
                <Icon name="file" size={16} />
                <span>
                  <span className="file-name">{entry.nombre}</span>
                  <span className="muted">
                    {formatBytes(entry.tamano)}
                    {entry.modificado ? ` · ${dateTime(entry.modificado)}` : ''}
                  </span>
                </span>
              </span>
            ) : (
              <button
                type="button"
                className="file-row"
                onClick={() => open(path ? joinPath(path, entry.nombre) : entry.nombre)}
              >
                <Icon name={entry.tipo === 'unidad' ? 'disk' : 'folder'} size={16} />
                <span>
                  <span className="file-name">{entry.nombre}</span>
                  <span className="muted">
                    {entry.tipo === 'unidad' ? 'Unidad' : 'Carpeta'}
                    {entry.modificado ? ` · ${dateTime(entry.modificado)}` : ''}
                  </span>
                </span>
              </button>
            )}
            {entry.tipo === 'archivo' && files && (
              <button
                type="button"
                className="icon-button"
                aria-label={`Descargar ${entry.nombre}`}
                onClick={() => void download(entry)}
              >
                <Icon name="download" size={16} />
              </button>
            )}
          </li>
        ))}
      </ul>

      {transfers.length > 0 && (
        <section className="file-panel-transfers" aria-label="Transferencias">
          <div className="file-panel-transfers-head">
            <span>Transferencias · {transfers.length}</span>
            <button
              type="button"
              className="link-quiet"
              onClick={() => setTransfers((list) => list.filter((t) => t.status === 'curso'))}
            >
              Limpiar terminadas
            </button>
          </div>
          {transfers.map((t) => {
            const percent = t.total ? Math.round((t.done / t.total) * 100) : 0;
            return (
              <div key={t.key} className="file-transfer" data-status={t.status}>
                <div>
                  <Icon name={t.kind === 'descarga' ? 'download' : 'upload'} size={14} />
                  <span className="file-name">{t.name}</span>
                  <span className="muted">
                    {t.status === 'curso' &&
                      `${formatBytes(t.done)} / ${formatBytes(t.total)} · ${percent}% · ${formatBytes(t.rate)}/s`}
                    {t.status === 'listo' && (t.kind === 'descarga' ? 'Recibido' : 'Enviado')}
                    {t.status === 'cancelado' && 'Cancelado'}
                    {t.status === 'error' && t.message}
                  </span>
                  {t.cancel && (
                    <button type="button" className="icon-button" aria-label={`Cancelar ${t.name}`} onClick={t.cancel}>
                      <Icon name="close" size={13} />
                    </button>
                  )}
                </div>
                <progress max={t.total || 1} value={t.status === 'listo' ? t.total || 1 : t.done} />
              </div>
            );
          })}
        </section>
      )}
    </aside>
  );
}

import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { Card } from '@components/Card';
import { FilterSelect } from '@components/FilterSelect';
import { dateTime, shortDateTime } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';

const ENTITY_OPTIONS = [
  { value: 'dispositivos_red', label: 'Dispositivos' },
  { value: 'puertos', label: 'Puertos' },
  { value: 'cableado', label: 'Cableado' },
  { value: 'puntos_red', label: 'Rosetas' },
  { value: 'gabinetes', label: 'Gabinetes' },
  { value: 'salas', label: 'Salas' },
  { value: 'modelos_dispositivo', label: 'Catálogo' },
];

const DAY_OPTIONS = [
  { value: '7', label: 'Últimos 7 días' },
  { value: '30', label: 'Últimos 30 días' },
  { value: '90', label: 'Últimos 90 días' },
  { value: '365', label: 'Último año' },
];

interface NetworkHistoryProps {
  /** Solo el historial de un objeto (por ejemplo, un dispositivo y sus puertos); sin filtros. */
  entity?: string;
  entityId?: number;
  /** Con título, el historial de un objeto va en su propio panel, como bitácora, con la cuenta de eventos. */
  cardTitle?: string;
}

/** Color del punto de un evento según lo que dice (fallo en rojo, alta o respuesta en verde). */
export function eventTone(title: string): 'critico' | 'advertencia' | 'sano' | 'info' {
  const text = title.toLowerCase();
  if (/fall|caíd|caid|error|baja|sin respuesta|desconect/.test(text)) return 'critico';
  if (/advert|errores|reinici/.test(text)) return 'advertencia';
  if (/registr|respond|conect|alta|cread/.test(text)) return 'sano';
  return 'info';
}

/** W-69 Historial del data center: quién cambió qué y cuándo, con lo que cambió. */
export function NetworkHistory({ entity, entityId, cardTitle }: NetworkHistoryProps) {
  const api = usePanelApi();
  const fixed = entity !== undefined;
  const [entidad, setEntidad] = useState('');
  const [dias, setDias] = useState(fixed ? '365' : '30');
  const [pagina, setPagina] = useState(1);
  const filter = {
    entidad: fixed ? entity : entidad || null,
    id: entityId === undefined ? null : String(entityId),
    dias: Number(dias),
    pagina,
  };
  const history = useQuery({
    queryKey: ['red', 'historial', filter],
    queryFn: () => api.red.history(filter),
    placeholderData: keepPreviousData,
  });
  const pages = history.data ? Math.max(1, Math.ceil(history.data.total / history.data.pageSize)) : 1;

  if (cardTitle) {
    const total = history.data?.total;
    return (
      <Card
        title={cardTitle}
        className="event-card"
        aside={
          total !== undefined && (
            <span className="eyebrow">
              {total} {total === 1 ? 'evento' : 'eventos'}
            </span>
          )
        }
      >
        {history.isPending ? (
          <Loading variant="list" rows={3} />
        ) : history.isError ? (
          <ErrorState error={history.error} onRetry={() => void history.refetch()} />
        ) : history.data.items.length === 0 ? (
          <EmptyState title="Sin cambios registrados." />
        ) : (
          <ul className="event-log" aria-label={cardTitle}>
            {history.data.items.map((item) => (
              <li key={item.id}>
                <span className="dot" data-status={eventTone(item.title)} aria-hidden="true" />
                <strong className="event-log-title">
                  {item.title}
                  {item.subject && <span className="mono"> · {item.subject}</span>}
                </strong>
                <time className="event-log-time mono" dateTime={item.at}>
                  {shortDateTime(item.at)}
                </time>
                <span className="event-log-src mono">{item.user ?? 'Sistema'}</span>
                {item.changes.length > 0 && (
                  <ul className="event-log-src history-changes">
                    {item.changes.map((c) => (
                      <li key={c.field}>
                        {c.field}: <span className="muted">{c.before ?? '—'}</span> → {c.after ?? '—'}
                      </li>
                    ))}
                  </ul>
                )}
              </li>
            ))}
          </ul>
        )}
      </Card>
    );
  }

  return (
    <>
      {!fixed && (
        <div className="filters">
          <FilterSelect
            label="Objeto"
            value={entidad}
            options={ENTITY_OPTIONS}
            allLabel="Todo"
            onChange={(v) => {
              setEntidad(v);
              setPagina(1);
            }}
          />
          <FilterSelect
            label="Periodo"
            value={dias}
            options={DAY_OPTIONS}
            onChange={(v) => {
              setDias(v);
              setPagina(1);
            }}
          />
        </div>
      )}
      {history.isPending ? (
        <Loading variant="list" />
      ) : history.isError ? (
        <ErrorState error={history.error} onRetry={() => void history.refetch()} />
      ) : history.data.items.length === 0 ? (
        <EmptyState title="Sin cambios en este periodo." />
      ) : (
        <div className={fixed ? undefined : 'table-card'}>
          <ul className={fixed ? 'timeline' : 'timeline history-list'} aria-label="Historial del data center">
            {history.data.items.map((item) => (
              <li key={item.id}>
                <strong>
                  {item.title}
                  {item.subject && <span className="mono"> · {item.subject}</span>}
                </strong>
                {item.changes.length > 0 && (
                  <ul className="history-changes">
                    {item.changes.map((c) => (
                      <li key={c.field}>
                        {c.field}: <span className="muted">{c.before ?? '—'}</span> → {c.after ?? '—'}
                      </li>
                    ))}
                  </ul>
                )}
                <span className="muted">
                  {item.user ?? 'Sistema'} · {dateTime(item.at)}
                </span>
              </li>
            ))}
          </ul>
          {pages > 1 && (
            <div className="pager">
              <button
                type="button"
                className="btn btn-outline"
                disabled={pagina <= 1}
                onClick={() => setPagina(pagina - 1)}
              >
                Anterior
              </button>
              <span className="muted">
                Página {pagina} de {pages}
              </span>
              <button
                type="button"
                className="btn btn-outline"
                disabled={pagina >= pages}
                onClick={() => setPagina(pagina + 1)}
              >
                Siguiente
              </button>
            </div>
          )}
        </div>
      )}
    </>
  );
}

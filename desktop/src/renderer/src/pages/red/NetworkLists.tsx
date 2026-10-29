import { useState } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { DeviceModelItem, OutletItem } from '@monitoreoti/shared';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { Icon } from '@components/Icon';
import { StatusBadge } from '@components/StatusBadge';
import { timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { useDebounced } from '../../data/useDebounced';
import { DeviceForm, ModelForm, OutletForm, PathDialog } from './NetworkForms';
import { DEVICE_STATUS_LABEL, DEVICE_STATUS_TONE, DEVICE_TYPE_LABEL, keysOf } from './netLabels';

const TYPE_OPTIONS = keysOf(DEVICE_TYPE_LABEL).map((t) => ({ value: t, label: DEVICE_TYPE_LABEL[t] }));

/** W-48 Dispositivos de red. */
export function DevicesList({ canEdit, onOpenDevice }: { canEdit: boolean; onOpenDevice: (id: number) => void }) {
  const api = usePanelApi();
  const [query, setQuery] = useState('');
  const [tipo, setTipo] = useState('');
  const [sucursal, setSucursal] = useState('');
  const [adding, setAdding] = useState(false);
  const search = useDebounced(query.trim());
  const filter = { q: search, tipo, sucursal: sucursal ? Number(sucursal) : null };
  const list = useQuery({
    queryKey: ['red', 'dispositivos', filter],
    queryFn: () => api.red.devices(filter),
    placeholderData: keepPreviousData,
  });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });

  return (
    <>
      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="Buscar por nombre, IP o modelo"
            aria-label="Buscar dispositivo"
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
        <FilterSelect label="Tipo" value={tipo} options={TYPE_OPTIONS} allLabel="Todos" onChange={setTipo} />
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
        {canEdit && (
          <button type="button" className="btn btn-primary filters-action" onClick={() => setAdding(true)}>
            Agregar dispositivo
          </button>
        )}
      </div>
      {list.isPending ? (
        <Loading />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Estado</th>
                <th>Nombre</th>
                <th>Tipo</th>
                <th>Ubicación</th>
                <th>IP de gestión</th>
                <th>Puertos</th>
                <th>Último sondeo</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((d) => (
                <tr key={d.id}>
                  <td>
                    <StatusBadge status={DEVICE_STATUS_TONE[d.status]} label={DEVICE_STATUS_LABEL[d.status]} />
                  </td>
                  <td>
                    <button type="button" className="row-name mono" onClick={() => onOpenDevice(d.id)}>
                      {d.name}
                    </button>
                    <span className="row-sub">
                      {d.brand} {d.modelName}
                    </span>
                  </td>
                  <td>{DEVICE_TYPE_LABEL[d.type]}</td>
                  <td>
                    {d.rackName ? `${d.rackName} · U${d.positionU}` : 'Fuera de gabinete'}
                    <span className="row-sub">
                      {d.branchName}
                      {d.roomName && ` · ${d.roomName}`}
                    </span>
                  </td>
                  <td className="mono">{d.managementIp ?? '—'}</td>
                  <td className="mono">
                    {d.portCount === 0 ? '—' : `${d.portsConnected}/${d.portCount}`}
                    {d.portsWithProblems > 0 && (
                      <span className="row-sub is-late">{d.portsWithProblems} con problemas</span>
                    )}
                  </td>
                  <td className="mono">{d.type === 'patch_panel' ? '—' : timeAgo(d.lastPolledAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.data.length === 0 && <EmptyState title="Ningún dispositivo coincide con los filtros." />}
        </div>
      )}
      {adding && <DeviceForm onClose={() => setAdding(false)} onSaved={onOpenDevice} />}
    </>
  );
}

/** W-64 Puntos de red (rosetas). */
export function OutletsList({ canEdit }: { canEdit: boolean }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [query, setQuery] = useState('');
  const [sucursal, setSucursal] = useState('');
  const [editing, setEditing] = useState<OutletItem | 'nueva' | null>(null);
  const [route, setRoute] = useState<OutletItem | null>(null);
  const search = useDebounced(query.trim());
  const branchId = sucursal ? Number(sucursal) : null;
  const list = useQuery({
    queryKey: ['red', 'rosetas', branchId, search],
    queryFn: () => api.red.outlets(branchId, search),
    placeholderData: keepPreviousData,
  });
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const remove = useMutation({
    mutationFn: (id: number) => api.red.deleteOutlet(id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['red'] }),
  });

  return (
    <>
      <div className="filters">
        <label className="search filter-search">
          <Icon name="search" />
          <input
            type="search"
            value={query}
            placeholder="Buscar por código o ubicación"
            aria-label="Buscar roseta"
            onChange={(e) => setQuery(e.target.value)}
          />
        </label>
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
        {canEdit && (
          <button type="button" className="btn btn-primary filters-action" onClick={() => setEditing('nueva')}>
            Agregar roseta
          </button>
        )}
      </div>
      {remove.isError && (
        <p className="auth-error" role="alert">
          {remove.error.message}
        </p>
      )}
      {list.isPending ? (
        <Loading />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Código</th>
                <th>Ubicación</th>
                <th>Sucursal</th>
                <th>Conectada a</th>
                <th aria-label="Acciones" />
              </tr>
            </thead>
            <tbody>
              {list.data.map((o) => (
                <tr key={o.id}>
                  <td className="mono">{o.code}</td>
                  <td>{o.location ?? '—'}</td>
                  <td>{o.branchName}</td>
                  <td className="mono">{o.portLabel ?? <span className="muted">Sin cable</span>}</td>
                  <td>
                    <div className="row-actions">
                      <button type="button" className="btn btn-outline" onClick={() => setRoute(o)}>
                        Ruta
                      </button>
                      {canEdit && (
                        <>
                          <button type="button" className="btn btn-outline" onClick={() => setEditing(o)}>
                            Editar
                          </button>
                          <button
                            type="button"
                            className="btn btn-outline"
                            disabled={remove.isPending || o.cableId !== null}
                            title={o.cableId !== null ? 'Quite el cable primero' : undefined}
                            onClick={() => remove.mutate(o.id)}
                          >
                            Eliminar
                          </button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {list.data.length === 0 && (
            <EmptyState title="Sin rosetas">
              Registre las rosetas de pared o escritorio para documentar el cableado hasta el switch.
            </EmptyState>
          )}
        </div>
      )}
      {editing && <OutletForm outlet={editing === 'nueva' ? undefined : editing} onClose={() => setEditing(null)} />}
      {route && (
        <PathDialog
          from={{ roseta: route.id }}
          title={`Ruta desde la roseta ${route.code}`}
          onClose={() => setRoute(null)}
        />
      )}
    </>
  );
}

/** W-56 Catálogo de modelos. */
export function CatalogList({ canEdit }: { canEdit: boolean }) {
  const api = usePanelApi();
  const models = useQuery({ queryKey: ['red', 'modelos'], queryFn: () => api.red.models() });
  const [editing, setEditing] = useState<DeviceModelItem | 'nuevo' | null>(null);

  return (
    <>
      {canEdit && (
        <div className="filters">
          <p className="muted">El catálogo base es compartido; los modelos propios de la empresa se pueden editar.</p>
          <button type="button" className="btn btn-primary filters-action" onClick={() => setEditing('nuevo')}>
            Agregar modelo
          </button>
        </div>
      )}
      {models.isPending ? (
        <Loading />
      ) : models.isError ? (
        <ErrorState error={models.error} onRetry={() => void models.refetch()} />
      ) : (
        <div className="table-card">
          <table className="data-table">
            <thead>
              <tr>
                <th>Modelo</th>
                <th>Tipo</th>
                <th>Altura</th>
                <th>Puertos</th>
                <th>SNMP</th>
                <th>Origen</th>
                {canEdit && <th aria-label="Acciones" />}
              </tr>
            </thead>
            <tbody>
              {models.data.map((m) => (
                <tr key={m.id}>
                  <td>
                    <strong>{m.brand}</strong> {m.model}
                  </td>
                  <td>{DEVICE_TYPE_LABEL[m.type]}</td>
                  <td className="mono">{m.heightUnits} U</td>
                  <td>
                    <span className="mono">{m.portCount}</span>
                    {m.portDescription && <span className="row-sub">{m.portDescription}</span>}
                  </td>
                  <td>{m.isActiveDevice ? 'Sí' : 'Pasivo'}</td>
                  <td>{m.isShared ? 'Catálogo base' : 'Propio'}</td>
                  {canEdit && (
                    <td>
                      {!m.isShared && (
                        <button type="button" className="btn btn-outline" onClick={() => setEditing(m)}>
                          Editar
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && <ModelForm model={editing === 'nuevo' ? undefined : editing} onClose={() => setEditing(null)} />}
    </>
  );
}

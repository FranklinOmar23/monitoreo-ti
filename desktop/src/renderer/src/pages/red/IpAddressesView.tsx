import { useMemo, useState, type ReactNode } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { IpHistoryItem, IpItem, IpState, SubnetItem } from '@monitoreoti/shared';
import { Card } from '@components/Card';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { FilterSelect } from '@components/FilterSelect';
import { KpiCard } from '@components/KpiCard';
import { dateTime, shortDateTime, timeAgo } from '../../data/format';
import { usePanelApi } from '../../data/PanelData';
import { BranchSelect, FormModal, numberOrNull, textOrNull, useNetworkSave } from './NetworkForms';

const HOSTNAME_SOURCE: Record<'netbios' | 'dns' | 'fortigate', string> = {
  netbios: 'lo dio el equipo',
  dns: 'DNS',
  fortigate: 'del FortiGate',
};

export const IP_STATE_LABEL: Record<IpState, string> = {
  en_uso: 'En uso',
  sin_identificar: 'Sin identificar',
  reciente: 'Reciente',
  reservada: 'Reservada',
  libre: 'Libre',
};

const STATE_ORDER: IpState[] = ['en_uso', 'sin_identificar', 'reciente', 'reservada', 'libre'];

const SWEEP_OPTIONS: [number, string][] = [
  [0, 'Solo a mano'],
  [6, 'Cada 6 horas'],
  [12, 'Cada 12 horas'],
  [24, 'Cada día'],
  [168, 'Cada semana'],
];

/** Última parte de la IP para la celda del mapa: el último octeto en una /24 o menor; los dos últimos en una más grande. */
function cellLabel(ip: string, prefix: number): string {
  const parts = ip.split('.');
  return prefix >= 24 ? parts[3]! : `${parts[2]}.${parts[3]}`;
}

/**
 * Direcciones IP (decisión 72): cada subred con el estado de sus direcciones, para saber cuáles están libres de verdad
 * antes de ponerle una IP fija a un equipo. El recolector la barre con ping y la ARP del firewall dice quién está.
 */
export function IpAddressesView({ canEdit, onOpenEquipo }: { canEdit: boolean; onOpenEquipo: (id: number) => void }) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [sucursal, setSucursal] = useState('');
  const [chosen, setChosen] = useState<number | null>(null);
  const [editing, setEditing] = useState<SubnetItem | 'nueva' | null>(null);
  const branchId = sucursal ? Number(sucursal) : null;
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  const list = useQuery({
    queryKey: ['red', 'subredes', 'lista', branchId],
    queryFn: () => api.red.subnets(branchId),
    placeholderData: keepPreviousData,
  });
  const subnets = list.data ?? [];
  const subnetId = subnets.some((s) => s.id === chosen) ? chosen : (subnets[0]?.id ?? null);

  return (
    <>
      <div className="filters">
        <FilterSelect
          label="Sucursal"
          value={sucursal}
          options={(branches.data ?? []).map((b) => ({ value: String(b.id), label: b.name }))}
          allLabel="Todas"
          onChange={setSucursal}
        />
        {subnets.length > 0 && (
          <FilterSelect
            label="Subred"
            value={String(subnetId ?? '')}
            options={subnets.map((s) => ({ value: String(s.id), label: `${s.name} · ${s.cidr}` }))}
            onChange={(v) => setChosen(Number(v))}
          />
        )}
        {canEdit && (
          <button type="button" className="btn btn-primary filters-action" onClick={() => setEditing('nueva')}>
            Agregar subred
          </button>
        )}
      </div>
      {list.isPending ? (
        <Loading variant="dashboard" />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => void list.refetch()} />
      ) : subnetId === null ? (
        <div className="table-card">
          <EmptyState title="Sin subredes">
            Registre cada red de la empresa (por ejemplo 192.168.3.0/24) con su recolector. El recolector la barre con
            ping y lee la ARP del firewall: así se sabe qué IP fijas están en uso y cuáles se pueden asignar.
          </EmptyState>
        </div>
      ) : (
        <SubnetView
          key={subnetId}
          subnetId={subnetId}
          canEdit={canEdit}
          onEdit={(s) => setEditing(s)}
          onDeleted={() => {
            setChosen(null);
            void queries.invalidateQueries({ queryKey: ['red', 'subredes'] });
          }}
          onOpenEquipo={onOpenEquipo}
        />
      )}
      {editing && (
        <SubnetForm
          subnet={editing === 'nueva' ? undefined : editing}
          onClose={() => setEditing(null)}
          onSaved={(id) => setChosen(id)}
        />
      )}
    </>
  );
}

function SubnetView({
  subnetId,
  canEdit,
  onEdit,
  onDeleted,
  onOpenEquipo,
}: {
  subnetId: number;
  canEdit: boolean;
  onEdit: (subnet: SubnetItem) => void;
  onDeleted: () => void;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [selectedIp, setSelectedIp] = useState<string | null>(null);
  const [flash, setFlash] = useState<string | null>(null);
  /** "Ver en el mapa": la celda queda elegida, centrada, con foco y un destello para encontrarla. */
  const showOnMap = (ip: string) => {
    setSelectedIp(ip);
    setFlash(ip);
    requestAnimationFrame(() => {
      const cell = document.querySelector<HTMLButtonElement>(`.ip-cell[data-ip="${ip}"]`);
      cell?.scrollIntoView?.({ block: 'center', behavior: 'smooth' });
      cell?.focus({ preventScroll: true });
    });
    setTimeout(() => setFlash((current) => (current === ip ? null : current)), 1600);
  };
  const [hidden, setHidden] = useState<ReadonlySet<IpState>>(new Set());
  const [reserving, setReserving] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const detail = useQuery({
    queryKey: ['red', 'subredes', subnetId],
    queryFn: () => api.red.subnet(subnetId),
    // Mientras el recolector barre, se mira cada pocos segundos hasta que llegue el resultado.
    refetchInterval: (query) => (query.state.data?.subnet.sweepRequestedAt ? 4_000 : false),
  });
  const sweep = useMutation({
    mutationFn: () => api.red.sweepSubnet(subnetId),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['red', 'subredes'] }),
  });
  const removeReservation = useMutation({
    mutationFn: (id: number) => api.red.deleteReservation(subnetId, id),
    onSuccess: () => void queries.invalidateQueries({ queryKey: ['red', 'subredes'] }),
  });
  const removeSubnet = useMutation({
    mutationFn: () => api.red.deleteSubnet(subnetId),
    onSuccess: onDeleted,
  });

  const data = detail.data;
  const selected = useMemo(() => {
    if (!data) return null;
    const ip = selectedIp ?? data.suggested ?? data.addresses[0]?.ip;
    return data.addresses.find((a) => a.ip === ip) ?? null;
  }, [data, selectedIp]);

  if (detail.isPending) return <Loading variant="dashboard" />;
  if (detail.isError) return <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />;
  const { subnet, addresses, reservations, suggested } = detail.data;
  const prefix = Number(subnet.cidr.split('/')[1]);
  const sweeping = subnet.sweepRequestedAt !== null || sweep.isPending;

  return (
    <div className="ip-view">
      <div className="ip-head">
        <div className="ip-head-main">
          <h2 className="mono">{subnet.cidr}</h2>
          <p className="ip-meta">
            <span>{subnet.name}</span>
            {subnet.vlan !== null && (
              <span>
                VLAN <strong className="mono">{subnet.vlan}</strong>
              </span>
            )}
            {subnet.gateway && (
              <span>
                Puerta de enlace <strong className="mono">{subnet.gateway}</strong>
              </span>
            )}
            <span>
              {subnet.collectorName ? (
                <>
                  La barre <strong>{subnet.collectorName}</strong>
                  <span className="ip-online" data-online={subnet.collectorOnline || undefined}>
                    {subnet.collectorOnline ? 'en línea' : 'desconectado'}
                  </span>
                </>
              ) : (
                <span className="muted">Sin recolector</span>
              )}
            </span>
            {subnet.arpDeviceName && (
              <span>
                ARP de <strong>{subnet.arpDeviceName}</strong>
              </span>
            )}
          </p>
          <p className="ip-meta">
            <span>
              {sweeping ? (
                <span className="live">Escaneando…</span>
              ) : subnet.lastSweepAt ? (
                <>
                  Último barrido <strong>{timeAgo(subnet.lastSweepAt)}</strong>
                </>
              ) : (
                'Nunca se ha barrido'
              )}
            </span>
            <span>{SWEEP_OPTIONS.find(([h]) => h === subnet.sweepHours)?.[1] ?? `Cada ${subnet.sweepHours} h`}</span>
            <span>Libre tras {subnet.freeAfterDays} días sin verse</span>
          </p>
        </div>
        {canEdit && (
          <div className="ip-head-actions">
            <button
              type="button"
              className="btn btn-primary"
              disabled={sweeping || subnet.collectorId === null || !subnet.collectorCanSweep}
              title={
                subnet.collectorId === null
                  ? 'Asigne un recolector a la subred'
                  : !subnet.collectorCanSweep
                    ? 'El agente del recolector necesita la versión 0.3.4 o posterior'
                    : undefined
              }
              onClick={() => sweep.mutate()}
            >
              {sweeping ? 'Escaneando…' : 'Escanear ahora'}
            </button>
            <button type="button" className="btn btn-outline" onClick={() => onEdit(subnet)}>
              Editar
            </button>
            <button type="button" className="btn btn-outline btn-danger" onClick={() => setConfirmDelete(true)}>
              Eliminar
            </button>
          </div>
        )}
      </div>

      {sweep.isError && (
        <p className="auth-error" role="alert">
          {sweep.error.message}
        </p>
      )}
      {subnet.collectorId !== null && !subnet.collectorCanSweep && (
        <p className="ip-warn">
          El agente de <strong>{subnet.collectorName}</strong> es anterior a la versión 0.3.4 y no hace el barrido con
          ping. La subred se llena con la ARP{subnet.arpDeviceName ? ` de ${subnet.arpDeviceName}` : ''} en cada sondeo;
          actualice el agente para barrer todas las direcciones.
        </p>
      )}
      {subnet.lastError && !sweeping && (
        <div className="poll-error" role="alert">
          <strong>El último barrido falló</strong>
          <span>{subnet.lastError}</span>
        </div>
      )}

      <div className="kpi-grid">
        <KpiCard label="Direcciones" value={subnet.total} unit={subnet.cidr} />
        <KpiCard
          label="En uso"
          value={subnet.used}
          tone="sano"
          note={subnet.unidentified > 0 ? `${subnet.unidentified} sin identificar` : 'Todas identificadas'}
        />
        <KpiCard
          label="Recientes"
          value={subnet.recent}
          tone={subnet.recent > 0 ? 'advertencia' : 'neutral'}
          note="Vistas hace poco: no asignar"
        />
        <KpiCard label="Reservadas" value={subnet.reserved} />
        <KpiCard label="Libres" value={subnet.free} tone={subnet.free === 0 ? 'critico' : 'neutral'} />
      </div>

      {/* La clave reinicia "Copiada" cuando cambia la sugerida. */}
      <SuggestedIp key={suggested ?? 'ninguna'} ip={suggested} onShow={showOnMap} />

      <div className="ip-layout">
        <Card
          title="Mapa de direcciones"
          className="ip-map-card"
          aside={
            <div className="ip-legend" role="group" aria-label="Mostrar estados">
              {STATE_ORDER.map((state) => (
                <button
                  key={state}
                  type="button"
                  className="ip-legend-item"
                  data-state={state}
                  aria-pressed={!hidden.has(state)}
                  onClick={() => {
                    const next = new Set(hidden);
                    if (next.has(state)) next.delete(state);
                    else next.add(state);
                    setHidden(next);
                  }}
                >
                  <i aria-hidden="true" />
                  {IP_STATE_LABEL[state]}
                </button>
              ))}
            </div>
          }
        >
          <div className="ip-grid" data-wide={prefix < 24 || undefined}>
            {addresses.map((a) => (
              <button
                key={a.ip}
                type="button"
                className="ip-cell mono"
                data-ip={a.ip}
                data-state={a.state}
                data-flash={flash === a.ip || undefined}
                data-dim={hidden.has(a.state) || undefined}
                data-warn={a.macChanged || undefined}
                aria-pressed={selected?.ip === a.ip}
                aria-label={`${a.ip}: ${IP_STATE_LABEL[a.state]}${(a.machineName ?? a.hostname) ? `, ${a.machineName ?? a.hostname}` : ''}`}
                title={`${a.ip} · ${IP_STATE_LABEL[a.state]}${(a.machineName ?? a.hostname) ? ` · ${a.machineName ?? a.hostname}` : ''}${a.reservation ? ` · ${a.reservation}` : ''}`}
                onClick={() => setSelectedIp(a.ip)}
              >
                {cellLabel(a.ip, prefix)}
              </button>
            ))}
          </div>
        </Card>
        <Card
          title="Detalle"
          className="ip-detail-card"
          aside={selected && <span className="eyebrow mono">{selected.ip}</span>}
        >
          {selected ? (
            <IpDetail key={selected.ip} ip={selected} subnet={subnet} onOpenEquipo={onOpenEquipo} />
          ) : (
            <EmptyState title="Elija una dirección del mapa." />
          )}
        </Card>
      </div>

      <Card
        title="Reservas"
        aside={
          canEdit && (
            <button type="button" className="btn btn-outline" onClick={() => setReserving(true)}>
              Reservar rango
            </button>
          )
        }
      >
        {removeReservation.isError && (
          <p className="auth-error" role="alert">
            {removeReservation.error.message}
          </p>
        )}
        {reservations.length === 0 ? (
          <EmptyState title="Sin reservas">
            Reserve el rango del DHCP y las direcciones de impresoras o cámaras para que nunca se sugieran.
          </EmptyState>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Desde</th>
                <th>Hasta</th>
                <th>Direcciones</th>
                <th>Motivo</th>
                {canEdit && <th aria-label="Acciones" />}
              </tr>
            </thead>
            <tbody>
              {reservations.map((r) => (
                <tr key={r.id}>
                  <td className="mono">{r.from}</td>
                  <td className="mono">{r.to}</td>
                  <td className="mono">{r.count}</td>
                  <td>
                    {r.reason}
                    {r.origin === 'fortigate' && <span className="tag-soft">del FortiGate</span>}
                  </td>
                  {canEdit && (
                    <td className="row-actions">
                      {r.origin === 'fortigate' ? (
                        <span className="muted" title="Se cambia en el DHCP del FortiGate y aquí se actualiza sola">
                          Automática
                        </span>
                      ) : (
                        <button
                          type="button"
                          className="btn btn-outline"
                          disabled={removeReservation.isPending}
                          onClick={() => removeReservation.mutate(r.id)}
                        >
                          Quitar
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Card>

      {reserving && <ReservationForm subnet={subnet} onClose={() => setReserving(false)} />}
      {confirmDelete && (
        <FormModal
          id="subnet-delete"
          title={`Eliminar la subred ${subnet.cidr}`}
          pending={removeSubnet.isPending}
          error={removeSubnet.error}
          onClose={() => setConfirmDelete(false)}
          onSubmit={() => removeSubnet.mutate()}
        >
          <p className="form-span">
            Se borra la subred con su historial de direcciones y sus reservas. Los equipos y la red no cambian.
          </p>
        </FormModal>
      )}
    </div>
  );
}

function SuggestedIp({ ip, onShow }: { ip: string | null; onShow: (ip: string) => void }) {
  const [copied, setCopied] = useState(false);
  if (!ip)
    return (
      <div className="ip-suggest" data-empty>
        <span className="eyebrow">IP libre sugerida</span>
        <span>No queda ninguna dirección libre: libere una reserva o revise las recientes.</span>
      </div>
    );
  // El portapapeles pasa por el proceso principal: el panel niega los permisos web, incluido el de portapapeles.
  const copy = () => {
    const write = window.monitoreo ? window.monitoreo.copyText(ip) : navigator.clipboard.writeText(ip);
    void write.then(
      () => setCopied(true),
      () => setCopied(false),
    );
  };
  return (
    <div className="ip-suggest">
      <span className="eyebrow">IP libre sugerida</span>
      <strong className="mono">{ip}</strong>
      <span className="muted">Nadie la ha usado en el tiempo de la subred y no está reservada.</span>
      <span className="ip-suggest-actions">
        <button type="button" className="btn btn-outline" onClick={() => onShow(ip)}>
          Ver en el mapa
        </button>
        <button type="button" className="btn btn-outline" onClick={copy}>
          {copied ? 'Copiada' : 'Copiar'}
        </button>
      </span>
    </div>
  );
}

function IpDetail({
  ip,
  subnet,
  onOpenEquipo,
}: {
  ip: IpItem;
  subnet: SubnetItem;
  onOpenEquipo: (id: number) => void;
}) {
  const note =
    ip.state === 'libre'
      ? `Nadie la usó en los últimos ${subnet.freeAfterDays} días: se puede asignar.`
      : ip.state === 'reciente'
        ? `Se vio ${timeAgo(ip.lastSeenAt)}: puede ser un equipo apagado. Mejor no asignarla.`
        : ip.state === 'sin_identificar'
          ? 'Está en uso, pero no se sabe qué equipo es. Revise la MAC y el puerto del switch.'
          : null;

  const rows: [string, ReactNode, boolean?][] = [
    [
      'Equipo',
      ip.machineId !== null ? (
        <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(ip.machineId!)}>
          {ip.machineName}
        </button>
      ) : null,
    ],
    [
      ip.machineId !== null && ip.hostname?.toLowerCase() === ip.machineName?.toLowerCase()
        ? 'Nombre en la red'
        : 'Nombre',
      ip.hostname ? `${ip.hostname} (${HOSTNAME_SOURCE[ip.hostnameSource ?? 'dns']})` : null,
    ],
    ['Sistema', ip.operatingSystem ?? null],
    [
      'Asignación',
      ip.assignment === 'dhcp'
        ? `DHCP del FortiGate${ip.dhcpExpiresAt ? ` · vence ${shortDateTime(ip.dhcpExpiresAt)}` : ''}`
        : ip.assignment === 'fija'
          ? 'Fija (no la dio el DHCP)'
          : null,
    ],
    [
      'Cómo se sabe',
      ip.machineSource === 'agente' ? 'La reporta su agente' : ip.machineSource === 'mac' ? 'Por su MAC' : null,
    ],
    ['MAC', ip.mac, true],
    ['Fabricante', ip.vendor],
    ['Interfaz', ip.interface ?? null, true],
    ['Puerto', ip.port, true],
    ['Reserva', ip.reservation],
    ['Primera vez', ip.firstSeenAt ? dateTime(ip.firstSeenAt) : null, true],
    ['Última vez', ip.lastSeenAt ? timeAgo(ip.lastSeenAt) : null, true],
    ['Ping', ip.lastPingAt ? `Respondió ${timeAgo(ip.lastPingAt)}` : ip.lastSeenAt ? 'No responde (firewall)' : null],
  ];

  return (
    <div className="ip-detail">
      <div className="ip-detail-head">
        <strong className="mono">{ip.ip}</strong>
        <span className="ip-state" data-state={ip.state}>
          {IP_STATE_LABEL[ip.state]}
        </span>
      </div>
      {ip.macChanged && (
        <p className="ip-warn" role="alert">
          Cambió de MAC: antes era <span className="mono">{ip.previousMac}</span>. Puede ser un equipo reemplazado o dos
          equipos con la misma IP.
        </p>
      )}
      {note && <p className="ip-note">{note}</p>}
      <dl className="info-list">
        {rows
          .filter(([, value]) => value !== null && value !== undefined)
          .map(([label, value, mono]) => (
            <div key={label}>
              <dt>{label}</dt>
              <dd className={mono ? 'mono strong' : undefined}>{value}</dd>
            </div>
          ))}
      </dl>
      <IpHistory subnetId={subnet.id} ip={ip.ip} onOpenEquipo={onOpenEquipo} />
    </div>
  );
}

const HISTORY_DAYS = [7, 30, 90] as const;

const SOURCE_LABEL: Record<IpHistoryItem['source'], string> = {
  arp: 'ARP',
  ping: 'Ping',
  agente: 'Agente',
};

/** Quién tuvo la dirección en los últimos días: un período por equipo, lo más reciente arriba. */
function IpHistory({
  subnetId,
  ip,
  onOpenEquipo,
}: {
  subnetId: number;
  ip: string;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const [days, setDays] = useState<number>(30);
  const history = useQuery({
    queryKey: ['red', 'subredes', subnetId, 'historial', ip, days],
    queryFn: () => api.red.ipHistory(subnetId, ip, days),
  });

  return (
    <section className="ip-history" aria-label={`Historial de ${ip}`}>
      <header className="ip-history-head">
        <h3>Historial</h3>
        <div className="segmented" role="group" aria-label="Período">
          {HISTORY_DAYS.map((d) => (
            <button key={d} type="button" aria-pressed={days === d} onClick={() => setDays(d)}>
              {d} días
            </button>
          ))}
        </div>
      </header>
      {history.isPending ? (
        <Loading variant="list" rows={2} />
      ) : history.isError ? (
        <ErrorState error={history.error} onRetry={() => void history.refetch()} />
      ) : history.data.length === 0 ? (
        <p className="ip-note">Nadie la usó en los últimos {days} días.</p>
      ) : (
        <ol className="ip-history-list">
          {history.data.map((h) => (
            <li key={`${h.from}-${h.mac ?? h.machineId ?? ''}`} data-current={h.current || undefined}>
              <span className="ip-history-who">
                {h.machineId !== null && h.machineName ? (
                  <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(h.machineId!)}>
                    {h.machineName}
                  </button>
                ) : (
                  <span className="mono">{h.machineName ?? h.mac ?? 'Equipo sin identificar'}</span>
                )}
                {h.current && <span className="ip-history-now">Ahora</span>}
              </span>
              <span className="ip-history-when mono">
                {shortDateTime(h.from)} → {h.current ? 'ahora' : shortDateTime(h.to)}
              </span>
              <span className="ip-history-meta">
                {[h.machineName && h.mac ? h.mac : null, h.vendor, SOURCE_LABEL[h.source]].filter(Boolean).join(' · ')}
              </span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

// ------------------------------------------------------------------ formularios

export function SubnetForm({
  subnet,
  onClose,
  onSaved,
}: {
  subnet?: SubnetItem;
  onClose: () => void;
  onSaved?: (id: number) => void;
}) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    nombre: subnet?.name ?? '',
    sucursalId: subnet ? String(subnet.branchId) : '',
    red: subnet?.cidr ?? '',
    vlan: subnet?.vlan ? String(subnet.vlan) : '',
    puertaEnlace: subnet?.gateway ?? '',
    recolectorId: subnet?.collectorId ? String(subnet.collectorId) : '',
    dispositivoArpId: subnet?.arpDeviceId ? String(subnet.arpDeviceId) : '',
    barridoHoras: String(subnet?.sweepHours ?? 24),
    diasLibre: String(subnet?.freeAfterDays ?? 30),
  });
  const branchId = form.sucursalId ? Number(form.sucursalId) : null;
  const collectors = useQuery({
    queryKey: ['red', 'recolectores', branchId],
    queryFn: () => api.red.collectors(branchId),
    enabled: branchId !== null,
  });
  const devices = useQuery({
    queryKey: ['red', 'dispositivos', 'arp', branchId],
    queryFn: () => api.red.devices({ sucursal: branchId }),
    enabled: branchId !== null,
  });
  const arpCandidates = (devices.data ?? []).filter(
    (d) => d.managementIp && ['firewall', 'router', 'switch'].includes(d.type),
  );
  const save = useNetworkSave(
    () =>
      api.red.saveSubnet(subnet?.id ?? null, {
        nombre: form.nombre.trim(),
        sucursalId: Number(form.sucursalId),
        red: form.red.trim(),
        vlan: numberOrNull(form.vlan),
        puertaEnlace: textOrNull(form.puertaEnlace),
        recolectorId: numberOrNull(form.recolectorId),
        dispositivoArpId: numberOrNull(form.dispositivoArpId),
        barridoHoras: Number(form.barridoHoras),
        diasLibre: Number(form.diasLibre),
      }),
    (id) => {
      onSaved?.(id);
      onClose();
    },
  );
  const changingRange = subnet !== undefined && form.red.trim() !== subnet.cidr;

  return (
    <FormModal
      id="subnet-form"
      title={subnet ? `Editar ${subnet.name}` : 'Agregar subred'}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
      wide
    >
      <label className="field">
        <span>Nombre *</span>
        <input
          required
          maxLength={100}
          value={form.nombre}
          placeholder="Datos Santo Domingo"
          onChange={(e) => setForm({ ...form, nombre: e.target.value })}
          autoFocus
        />
      </label>
      <BranchSelect
        value={form.sucursalId}
        onChange={(v) => setForm({ ...form, sucursalId: v, recolectorId: '', dispositivoArpId: '' })}
      />
      <label className="field">
        <span>Red *</span>
        <input
          required
          className="mono"
          value={form.red}
          placeholder="192.168.3.0/24"
          onChange={(e) => setForm({ ...form, red: e.target.value })}
        />
        <small className="field-hint">De /22 (1022 direcciones) a /30.</small>
      </label>
      <label className="field">
        <span>VLAN</span>
        <input
          type="number"
          min={1}
          max={4094}
          value={form.vlan}
          onChange={(e) => setForm({ ...form, vlan: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Puerta de enlace</span>
        <input
          className="mono"
          value={form.puertaEnlace}
          placeholder="192.168.3.1"
          onChange={(e) => setForm({ ...form, puertaEnlace: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Recolector</span>
        <select
          value={form.recolectorId}
          disabled={branchId === null}
          onChange={(e) => setForm({ ...form, recolectorId: e.target.value })}
        >
          <option value="">Sin recolector (no se barre)</option>
          {(collectors.data ?? []).map((c) => (
            <option key={c.agentId} value={c.agentId}>
              {c.machineName}
              {c.online ? '' : ' (desconectado)'}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Leer la ARP de</span>
        <select
          value={form.dispositivoArpId}
          disabled={branchId === null}
          onChange={(e) => setForm({ ...form, dispositivoArpId: e.target.value })}
        >
          <option value="">Solo la del recolector</option>
          {arpCandidates.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name} · {d.managementIp}
            </option>
          ))}
        </select>
        <small className="field-hint">
          El firewall o router de la VLAN, consultado por SNMP por este mismo recolector.
        </small>
      </label>
      <label className="field">
        <span>Barrido automático</span>
        <select value={form.barridoHoras} onChange={(e) => setForm({ ...form, barridoHoras: e.target.value })}>
          {SWEEP_OPTIONS.map(([hours, label]) => (
            <option key={hours} value={hours}>
              {label}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Libre tras (días sin verse)</span>
        <select value={form.diasLibre} onChange={(e) => setForm({ ...form, diasLibre: e.target.value })}>
          {[7, 15, 30, 60, 90].map((days) => (
            <option key={days} value={days}>
              {days} días
            </option>
          ))}
        </select>
      </label>
      {changingRange && (
        <p className="ip-warn form-span" role="alert">
          Al cambiar la red se borra lo visto y las reservas de la red anterior.
        </p>
      )}
    </FormModal>
  );
}

function ReservationForm({ subnet, onClose }: { subnet: SubnetItem; onClose: () => void }) {
  const api = usePanelApi();
  const base = subnet.cidr.split('/')[0]!.split('.').slice(0, 3).join('.');
  const [form, setForm] = useState({ desde: `${base}.`, hasta: `${base}.`, motivo: '' });
  const save = useNetworkSave(
    () =>
      api.red.addReservation(subnet.id, {
        desde: form.desde.trim(),
        hasta: form.hasta.trim(),
        motivo: form.motivo.trim(),
      }),
    onClose,
  );
  return (
    <FormModal
      id="reservation-form"
      title={`Reservar en ${subnet.cidr}`}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <label className="field">
        <span>Desde *</span>
        <input
          required
          className="mono"
          value={form.desde}
          onChange={(e) => setForm({ ...form, desde: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Hasta *</span>
        <input
          required
          className="mono"
          value={form.hasta}
          onChange={(e) => setForm({ ...form, hasta: e.target.value })}
        />
      </label>
      <label className="field form-span">
        <span>Motivo *</span>
        <input
          required
          maxLength={150}
          value={form.motivo}
          placeholder="Rango del DHCP"
          onChange={(e) => setForm({ ...form, motivo: e.target.value })}
        />
      </label>
    </FormModal>
  );
}

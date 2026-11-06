import { useEffect, useRef, useState } from 'react';
import { Pager } from '@components/Pager';
import { usePaged } from '../../data/usePaged';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { IpItem, RemoteInstallItem, RemoteInstallStatus, SubnetItem } from '@monitoreoti/shared';
import { Card } from '@components/Card';
import { usePanelApi } from '../../data/PanelData';
import { timeAgo } from '../../data/format';
import { needsTwoFactor, TwoFactorPrompt } from '../remoto/TwoFactorPrompt';
import { FormModal } from './NetworkForms';

export const INSTALL_STATUS_LABEL: Record<RemoteInstallStatus, string> = {
  pendiente: 'En cola',
  conectando: 'Conectando',
  copiando: 'Copiando',
  instalando: 'Instalando',
  instalado: 'Esperando registro',
  registrado: 'Registrado',
  error: 'Error',
  omitido: 'Omitido',
};

const FINAL: ReadonlySet<RemoteInstallStatus> = new Set(['registrado', 'error', 'omitido']);

/** Una IP donde tiene sentido instalar: en uso, sin agente y no la puerta de enlace. */
export const canInstallOn = (ip: IpItem) =>
  !ip.isGateway && ip.machineSource !== 'agente' && (ip.state === 'sin_identificar' || ip.state === 'en_uso');

/** Las que parecen PCs con Windows (el FortiGate dijo Windows o respondió por NetBIOS). */
export const looksLikeWindows = (ip: IpItem) =>
  !!ip.operatingSystem?.toLowerCase().includes('windows') || ip.hostnameSource === 'netbios';

/** La cuenta del dominio que se recuerda en este equipo (solo el usuario; la clave nunca). */
export const SAVED_ACCOUNT_KEY = 'centinela.instalar.cuenta';

const readSavedAccount = () => {
  try {
    return localStorage.getItem(SAVED_ACCOUNT_KEY) ?? '';
  } catch {
    return '';
  }
};

const writeSavedAccount = (account: string | null) => {
  try {
    if (account) localStorage.setItem(SAVED_ACCOUNT_KEY, account);
    else localStorage.removeItem(SAVED_ACCOUNT_KEY);
  } catch {
    // Sin almacenamiento: simplemente no se recuerda.
  }
};

const parseIps = (text: string) =>
  [...new Set(text.split(/[\s,;]+/).map((s) => s.trim()))].filter((s) => /^\d{1,3}(\.\d{1,3}){3}$/.test(s));

/**
 * "Instalar agente" (decisión 77): el recolector de la subred entra a cada equipo con la cuenta del dominio, copia el
 * instalador y lo ejecuta. La clave se manda sellada al recolector y no se guarda; pide el 2FA reciente.
 */
export function InstallAgentDialog({
  subnet,
  ips,
  onClose,
}: {
  subnet: SubnetItem;
  ips: string[];
  onClose: () => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const [saved] = useState(readSavedAccount);
  const [form, setForm] = useState({ ips: ips.join('\n'), cuenta: saved, clave: '', recordar: true });
  const [askCode, setAskCode] = useState(false);
  const claveRef = useRef<HTMLInputElement>(null);
  // Con la cuenta ya puesta, el cursor va a la clave (después de que el <dialog> tome el foco al abrirse).
  useEffect(() => {
    if (!saved) return;
    const timer = setTimeout(() => claveRef.current?.focus());
    return () => clearTimeout(timer);
  }, [saved]);
  const list = parseIps(form.ips);
  const install = useMutation({
    mutationFn: () => api.red.installAgents(subnet.id, { ips: list, cuenta: form.cuenta.trim(), clave: form.clave }),
    onSuccess: () => {
      writeSavedAccount(form.recordar ? form.cuenta.trim() : null);
      void queries.invalidateQueries({ queryKey: ['red', 'instalaciones', subnet.id] });
      onClose();
    },
    onError: (error) => {
      if (needsTwoFactor(error)) setAskCode(true);
    },
  });

  return (
    <>
      <FormModal
        id="install-agent-form"
        title="Instalar agente"
        pending={install.isPending}
        error={install.error && !needsTwoFactor(install.error) ? install.error : null}
        onClose={onClose}
        onSubmit={() => install.mutate()}
        submitLabel={list.length > 1 ? `Instalar en ${list.length} equipos` : 'Instalar'}
      >
        <p className="muted form-span install-note">
          Lo instala <strong>{subnet.collectorName ?? 'el recolector'}</strong>: entra a cada equipo por la red (puerto
          445, ADMIN$) con la cuenta del dominio, copia el instalador y lo ejecuta en silencio. El equipo queda
          registrado en {subnet.branchName}. En Windows 10/11 instala el agente normal; en Windows 7 SP1, 8 o de 32
          bits, el agente clásico (monitoreo, inventario y scripts; necesita .NET Framework 4.5.2).
        </p>
        <label className="field form-span">
          <span>Direcciones IP *</span>
          <textarea
            required
            rows={Math.min(Math.max(list.length, 2), 8)}
            className="mono"
            value={form.ips}
            placeholder="192.168.4.6"
            onChange={(e) => setForm({ ...form, ips: e.target.value })}
          />
          <small className="muted">
            {list.length === 0 ? 'Una por línea.' : `${list.length} ${list.length === 1 ? 'equipo' : 'equipos'}.`}
          </small>
        </label>
        <label className="field">
          <span>Cuenta del dominio *</span>
          <input
            required
            autoComplete="off"
            maxLength={150}
            value={form.cuenta}
            placeholder="DOMINIO\usuario"
            onChange={(e) => setForm({ ...form, cuenta: e.target.value })}
          />
        </label>
        <label className="field">
          <span>Clave *</span>
          <input
            required
            type="password"
            autoComplete="new-password"
            ref={claveRef}
            maxLength={256}
            value={form.clave}
            onChange={(e) => setForm({ ...form, clave: e.target.value })}
          />
        </label>
        <label className="check form-span">
          <input
            type="checkbox"
            checked={form.recordar}
            onChange={(e) => {
              setForm({ ...form, recordar: e.target.checked });
              // Desmarcarlo olvida la cuenta guardada en el acto.
              if (!e.target.checked) writeSavedAccount(null);
            }}
          />
          Guardar usuario (solo la cuenta, en este equipo; la clave se pide siempre)
        </label>
        <p className="muted form-span install-note">
          La clave viaja cifrada solo para el recolector y no se guarda en ningún lado. La auditoría registra quién lo
          pidió, en qué equipos y con qué cuenta.
        </p>
      </FormModal>
      {askCode && (
        <TwoFactorPrompt
          onConfirmed={() => {
            setAskCode(false);
            install.mutate();
          }}
          onCancel={() => setAskCode(false)}
        />
      )}
    </>
  );
}

/** Las instalaciones de las últimas 24 h; mientras alguna avanza se mira cada pocos segundos. */
export function RemoteInstallsCard({
  subnetId,
  onOpenEquipo,
}: {
  subnetId: number;
  onOpenEquipo: (id: number) => void;
}) {
  const api = usePanelApi();
  const queries = useQueryClient();
  const { data: page, items, setPage } = usePaged(
    ['red', 'instalaciones', subnetId],
    (p) => api.red.remoteInstalls(subnetId, p),
    { refetchInterval: (data) => (data?.data.some((i) => !FINAL.has(i.status)) ? 3_000 : false) },
  );
  const registered = items.filter((i) => i.status === 'registrado').length;
  const seen = useRef(registered);
  useEffect(() => {
    // Un equipo nuevo cambia el mapa de direcciones.
    if (registered > seen.current) void queries.invalidateQueries({ queryKey: ['red', 'subredes', subnetId] });
    seen.current = registered;
  }, [registered, queries, subnetId]);
  if (items.length === 0) return null;
  return (
    <Card title="Instalaciones del agente" aside={<span className="eyebrow">Últimas 24 h</span>}>
      <table className="data-table install-table">
        <thead>
          <tr>
            <th>IP</th>
            <th>Estado</th>
            <th>Sistema</th>
            <th>Detalle</th>
            <th>Pedida</th>
          </tr>
        </thead>
        <tbody>
          {items.map((i) => (
            <InstallRow key={i.id} item={i} onOpenEquipo={onOpenEquipo} />
          ))}
        </tbody>
      </table>
      <Pager data={page} noun="instalaciones" onPage={setPage} />
    </Card>
  );
}

function InstallRow({ item, onOpenEquipo }: { item: RemoteInstallItem; onOpenEquipo: (id: number) => void }) {
  return (
    <tr>
      <td className="mono">{item.ip}</td>
      <td>
        <span className="install-status" data-status={item.status}>
          {INSTALL_STATUS_LABEL[item.status]}
        </span>
      </td>
      <td>{item.operatingSystem ?? '—'}</td>
      <td>
        {item.machineId !== null ? (
          <button type="button" className="link-quiet mono" onClick={() => onOpenEquipo(item.machineId!)}>
            {item.machineName}
          </button>
        ) : (
          (item.detail ?? '—')
        )}
      </td>
      <td className="muted">
        {timeAgo(item.createdAt)}
        {item.userName ? ` · ${item.userName}` : ''} · <span className="mono">{item.account}</span>
      </td>
    </tr>
  );
}

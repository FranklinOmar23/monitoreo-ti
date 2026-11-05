import { useState, type FormEvent, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type {
  CableType,
  DeviceDetail,
  DeviceModelItem,
  DeviceType,
  OutletItem,
  PortItem,
  PortType,
  RackItem,
  RoomItem,
} from '@monitoreoti/shared';
import { Icon } from '@components/Icon';
import { Loading, ErrorState } from '@components/AsyncState';
import { Modal } from '@components/Modal';
import { usePanelApi } from '../../data/PanelData';
import { CABLE_TYPE_LABEL, DEVICE_TYPE_LABEL, PORT_TYPE_LABEL, keysOf } from './netLabels';
import { ApiTestButton, offersApi, suggestedApiUrl } from './FortiGateApi';

/** Guardar y refrescar todo lo del data center (las vistas comparten la clave ['red']). */
export function useNetworkSave<T>(fn: (value: T) => Promise<{ id: number }>, onSaved: (id: number) => void) {
  const queries = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: ({ id }) => {
      void queries.invalidateQueries({ queryKey: ['red'] });
      onSaved(id);
    },
  });
}

export function FormModal({
  id,
  title,
  pending,
  error,
  onClose,
  onSubmit,
  wide,
  submitLabel,
  children,
}: {
  id: string;
  title: string;
  pending: boolean;
  error: Error | null;
  onClose: () => void;
  onSubmit: () => void;
  wide?: boolean;
  /** Texto del botón (por defecto "Guardar"). */
  submitLabel?: string;
  children: ReactNode;
}) {
  const submit = (event: FormEvent) => {
    event.preventDefault();
    onSubmit();
  };
  return (
    <Modal
      title={title}
      onClose={onClose}
      wide={wide}
      footer={
        <>
          <button type="button" className="btn btn-outline" onClick={onClose}>
            Cancelar
          </button>
          <button type="submit" form={id} className="btn btn-primary" disabled={pending}>
            {pending ? (submitLabel ? 'Enviando…' : 'Guardando…') : (submitLabel ?? 'Guardar')}
          </button>
        </>
      }
    >
      <form id={id} className="form-grid" onSubmit={submit}>
        {error && (
          <p className="auth-error form-span" role="alert">
            {error.message}
          </p>
        )}
        {children}
      </form>
    </Modal>
  );
}

export const numberOrNull = (value: string) => (value.trim() === '' ? null : Number(value));
export const textOrNull = (value: string) => value.trim() || null;

export function BranchSelect({
  value,
  onChange,
  disabled,
}: {
  value: string;
  onChange: (v: string) => void;
  disabled?: boolean;
}) {
  const api = usePanelApi();
  const branches = useQuery({ queryKey: ['sucursales'], queryFn: () => api.branches() });
  return (
    <label className="field">
      <span>Sucursal *</span>
      <select required value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        <option value="">Elija…</option>
        {(branches.data ?? []).map((b) => (
          <option key={b.id} value={b.id}>
            {b.name}
          </option>
        ))}
      </select>
    </label>
  );
}

// ------------------------------------------------------------------ W-58 sala

export function RoomForm({ room, onClose }: { room?: RoomItem; onClose: () => void }) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    nombre: room?.name ?? '',
    ubicacion: room?.location ?? '',
    sucursalId: room ? String(room.branchId) : '',
  });
  const save = useNetworkSave(
    () =>
      api.red.saveRoom(room?.id ?? null, {
        nombre: form.nombre.trim(),
        ubicacion: textOrNull(form.ubicacion),
        sucursalId: Number(form.sucursalId),
      }),
    onClose,
  );
  return (
    <FormModal
      id="room-form"
      title={room ? `Editar ${room.name}` : 'Agregar sala'}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <label className="field form-span">
        <span>Nombre *</span>
        <input
          required
          maxLength={100}
          value={form.nombre}
          onChange={(e) => setForm({ ...form, nombre: e.target.value })}
          autoFocus
        />
      </label>
      <BranchSelect value={form.sucursalId} onChange={(v) => setForm({ ...form, sucursalId: v })} />
      <label className="field">
        <span>Ubicación</span>
        <input
          maxLength={200}
          value={form.ubicacion}
          placeholder="Piso 2, cuarto 204"
          onChange={(e) => setForm({ ...form, ubicacion: e.target.value })}
        />
      </label>
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-50 gabinete

export function RackForm({ rack, roomId, onClose }: { rack?: RackItem; roomId?: number; onClose: () => void }) {
  const api = usePanelApi();
  const rooms = useQuery({ queryKey: ['red', 'salas'], queryFn: () => api.red.rooms() });
  const [form, setForm] = useState({
    nombre: rack?.name ?? '',
    salaId: String(rack?.roomId ?? roomId ?? ''),
    alturaU: String(rack?.heightUnits ?? 42),
    fila: rack?.row ?? '',
    posicion: rack?.position ? String(rack.position) : '',
    activo: rack?.isActive ?? true,
  });
  const save = useNetworkSave(
    () =>
      api.red.saveRack(rack?.id ?? null, {
        nombre: form.nombre.trim(),
        salaId: Number(form.salaId),
        alturaU: Number(form.alturaU),
        fila: textOrNull(form.fila),
        posicion: numberOrNull(form.posicion),
        activo: form.activo,
      }),
    onClose,
  );
  return (
    <FormModal
      id="rack-form"
      title={rack ? `Editar ${rack.name}` : 'Agregar gabinete'}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <label className="field">
        <span>Nombre *</span>
        <input
          required
          maxLength={50}
          value={form.nombre}
          placeholder="GAB-A01"
          onChange={(e) => setForm({ ...form, nombre: e.target.value })}
          autoFocus
        />
      </label>
      <label className="field">
        <span>Sala *</span>
        <select required value={form.salaId} onChange={(e) => setForm({ ...form, salaId: e.target.value })}>
          <option value="">Elija…</option>
          {(rooms.data ?? []).map((r) => (
            <option key={r.id} value={r.id}>
              {r.name} · {r.branchName}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Altura (U) *</span>
        <input
          required
          type="number"
          min={1}
          max={60}
          value={form.alturaU}
          onChange={(e) => setForm({ ...form, alturaU: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Fila y posición en la sala</span>
        <span className="field-pair">
          <input
            maxLength={10}
            value={form.fila}
            placeholder="A"
            aria-label="Fila"
            onChange={(e) => setForm({ ...form, fila: e.target.value })}
          />
          <input
            type="number"
            min={1}
            value={form.posicion}
            placeholder="1"
            aria-label="Posición"
            onChange={(e) => setForm({ ...form, posicion: e.target.value })}
          />
        </span>
      </label>
      {rack && (
        <label className="check form-span">
          <input
            type="checkbox"
            checked={form.activo}
            onChange={(e) => setForm({ ...form, activo: e.target.checked })}
          />
          Gabinete activo
        </label>
      )}
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-57 modelo

export function ModelForm({ model, onClose }: { model?: DeviceModelItem; onClose: () => void }) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    marca: model?.brand ?? '',
    modelo: model?.model ?? '',
    tipo: (model?.type ?? 'switch') as DeviceType,
    alturaU: String(model?.heightUnits ?? 1),
    cantidadPuertos: String(model?.portCount ?? 24),
    tipoPuertos: model?.portDescription ?? '',
    esActivo: model?.isActiveDevice ?? true,
  });
  const save = useNetworkSave(
    () =>
      api.red.saveModel(model?.id ?? null, {
        marca: form.marca.trim(),
        modelo: form.modelo.trim(),
        tipo: form.tipo,
        alturaU: Number(form.alturaU),
        cantidadPuertos: Number(form.cantidadPuertos),
        tipoPuertos: textOrNull(form.tipoPuertos),
        esActivo: form.esActivo,
      }),
    onClose,
  );
  return (
    <FormModal
      id="model-form"
      title={model ? `Editar ${model.brand} ${model.model}` : 'Agregar modelo'}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <label className="field">
        <span>Marca *</span>
        <input
          required
          maxLength={100}
          value={form.marca}
          onChange={(e) => setForm({ ...form, marca: e.target.value })}
          autoFocus
        />
      </label>
      <label className="field">
        <span>Modelo *</span>
        <input
          required
          maxLength={100}
          value={form.modelo}
          onChange={(e) => setForm({ ...form, modelo: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Tipo</span>
        <select
          value={form.tipo}
          onChange={(e) => {
            const tipo = e.target.value as DeviceType;
            setForm({
              ...form,
              tipo,
              esActivo: tipo === 'patch_panel' || tipo === 'organizador' || tipo === 'bandeja' ? false : form.esActivo,
            });
          }}
        >
          {keysOf(DEVICE_TYPE_LABEL).map((t) => (
            <option key={t} value={t}>
              {DEVICE_TYPE_LABEL[t]}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Altura (U)</span>
        <input
          type="number"
          min={0}
          max={20}
          value={form.alturaU}
          onChange={(e) => setForm({ ...form, alturaU: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Cantidad de puertos</span>
        <input
          type="number"
          min={0}
          max={512}
          value={form.cantidadPuertos}
          onChange={(e) => setForm({ ...form, cantidadPuertos: e.target.value })}
        />
      </label>
      <label className="field">
        <span>Tipos de puerto</span>
        <input
          maxLength={100}
          value={form.tipoPuertos}
          placeholder="48 RJ45 + 4 SFP+"
          onChange={(e) => setForm({ ...form, tipoPuertos: e.target.value })}
        />
      </label>
      <label className="check form-span">
        <input
          type="checkbox"
          checked={form.esActivo}
          onChange={(e) => setForm({ ...form, esActivo: e.target.checked })}
        />
        Equipo activo (se consulta por SNMP). Desmarque para patch panels y otros pasivos.
      </label>
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-49 / W-61 dispositivo

export function DeviceForm({
  device,
  rackId,
  positionU,
  onClose,
  onSaved,
}: {
  device?: DeviceDetail;
  rackId?: number;
  positionU?: number;
  onClose: () => void;
  onSaved?: (id: number) => void;
}) {
  const api = usePanelApi();
  const models = useQuery({ queryKey: ['red', 'modelos'], queryFn: () => api.red.models() });
  const racks = useQuery({ queryKey: ['red', 'gabinetes'], queryFn: () => api.red.racks() });
  const [form, setForm] = useState({
    nombre: device?.name ?? '',
    modeloId: device ? String(device.modelId) : '',
    sucursalId: device ? String(device.branchId) : '',
    gabineteId: String(device?.rackId ?? rackId ?? ''),
    posicionU: String(device?.positionU ?? positionU ?? ''),
    ipGestion: device?.managementIp ?? '',
    firmware: device?.firmware ?? '',
    snmpUsuario: device?.snmpUser ?? '',
    auth: '',
    priv: '',
    recolectorId: device?.collectorId ? String(device.collectorId) : '',
    apiUrl: device?.apiUrl ?? '',
    apiToken: '',
    apiCert: device?.apiAcceptSelfSigned ?? true,
  });
  const model = models.data?.find((m) => String(m.id) === form.modeloId);
  const rack = racks.data?.find((r) => String(r.id) === form.gabineteId);
  // Si va en un gabinete, la sucursal es la del gabinete.
  const branchId = rack ? String(rack.branchId) : form.sucursalId;
  const active = model?.isActiveDevice ?? device?.isActiveDevice ?? true;
  const withApi = active && offersApi(model?.type ?? device?.type, device?.apiUrl);
  const apiSuggestion = suggestedApiUrl(form.ipGestion);
  // Recolectores posibles: equipos con agente de la sucursal del dispositivo.
  const collectors = useQuery({
    queryKey: ['red', 'recolectores', branchId],
    queryFn: () => api.red.collectors(Number(branchId)),
    enabled: active && !!branchId,
  });

  const save = useNetworkSave(
    () =>
      api.red.saveDevice(device?.id ?? null, {
        nombre: form.nombre.trim(),
        modeloId: Number(form.modeloId),
        sucursalId: Number(branchId),
        gabineteId: numberOrNull(form.gabineteId),
        posicionU: form.gabineteId ? numberOrNull(form.posicionU) : null,
        ipGestion: textOrNull(form.ipGestion),
        firmware: textOrNull(form.firmware),
        snmpUsuario: active ? textOrNull(form.snmpUsuario) : null,
        snmpClaveAutenticacion: active && form.auth ? form.auth : null,
        snmpClavePrivacidad: active && form.priv ? form.priv : null,
        recolectorId: active ? numberOrNull(form.recolectorId) : null,
        apiUrl: withApi ? form.apiUrl.trim() || null : null,
        apiToken: withApi && form.apiToken.trim() ? form.apiToken.trim() : null,
        apiAceptarCertificado: form.apiCert,
      }),
    (id) => {
      onSaved?.(id);
      onClose();
    },
  );

  return (
    <FormModal
      id="device-form"
      title={device ? `Editar ${device.name}` : 'Agregar dispositivo'}
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
          placeholder="SW-ACC-04"
          onChange={(e) => setForm({ ...form, nombre: e.target.value })}
          autoFocus
        />
      </label>
      <label className="field">
        <span>Modelo *</span>
        <select
          required
          disabled={!!device}
          value={form.modeloId}
          onChange={(e) => setForm({ ...form, modeloId: e.target.value })}
        >
          <option value="">Elija…</option>
          {(models.data ?? []).map((m) => (
            <option key={m.id} value={m.id}>
              {m.brand} {m.model} · {DEVICE_TYPE_LABEL[m.type]} · {m.heightUnits} U · {m.portCount} puertos
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Gabinete</span>
        <select value={form.gabineteId} onChange={(e) => setForm({ ...form, gabineteId: e.target.value })}>
          <option value="">Fuera de gabinete</option>
          {(racks.data ?? []).map((r) => (
            <option key={r.id} value={r.id}>
              {r.name} · {r.roomName} ({r.branchName})
            </option>
          ))}
        </select>
      </label>
      {form.gabineteId ? (
        <label className="field">
          <span>U inferior *</span>
          <input
            required
            type="number"
            min={1}
            max={rack?.heightUnits ?? 60}
            value={form.posicionU}
            onChange={(e) => setForm({ ...form, posicionU: e.target.value })}
          />
        </label>
      ) : (
        <BranchSelect value={form.sucursalId} onChange={(v) => setForm({ ...form, sucursalId: v })} />
      )}
      {active && (
        <>
          <label className="field">
            <span>IP de gestión</span>
            <input
              maxLength={45}
              value={form.ipGestion}
              placeholder="10.10.0.4"
              onChange={(e) => setForm({ ...form, ipGestion: e.target.value })}
            />
          </label>
          <label className="field">
            <span>Firmware</span>
            <input
              maxLength={100}
              value={form.firmware}
              onChange={(e) => setForm({ ...form, firmware: e.target.value })}
            />
          </label>
          <fieldset className="form-span snmp-fieldset">
            <legend>SNMPv3 de solo lectura (authPriv, SHA y AES)</legend>
            <div className="form-grid">
              <label className="field">
                <span>Usuario</span>
                <input
                  maxLength={100}
                  autoComplete="off"
                  value={form.snmpUsuario}
                  onChange={(e) => setForm({ ...form, snmpUsuario: e.target.value })}
                />
              </label>
              <span className="muted snmp-note">
                {device?.hasSnmpCredential
                  ? 'Hay claves guardadas: déjelas vacías para conservarlas.'
                  : 'Las claves se guardan cifradas y no se vuelven a mostrar.'}
              </span>
              <label className="field">
                <span>Clave de autenticación</span>
                <input
                  type="password"
                  autoComplete="new-password"
                  minLength={8}
                  maxLength={64}
                  value={form.auth}
                  onChange={(e) => setForm({ ...form, auth: e.target.value })}
                />
              </label>
              <label className="field">
                <span>Clave de privacidad</span>
                <input
                  type="password"
                  autoComplete="new-password"
                  minLength={8}
                  maxLength={64}
                  value={form.priv}
                  onChange={(e) => setForm({ ...form, priv: e.target.value })}
                />
              </label>
              <label className="field form-span">
                <span>Recolector (equipo con agente que lo consulta)</span>
                <select
                  value={form.recolectorId}
                  disabled={!branchId}
                  onChange={(e) => setForm({ ...form, recolectorId: e.target.value })}
                >
                  <option value="">Nadie lo consulta</option>
                  {(collectors.data ?? []).map((c) => (
                    <option key={c.agentId} value={c.agentId}>
                      {c.machineName}
                      {c.ipAddress ? ` · ${c.ipAddress}` : ''}
                      {c.online ? '' : ' · desconectado'}
                      {c.deviceCount > 0 ? ` · consulta ${c.deviceCount}` : ''}
                    </option>
                  ))}
                </select>
              </label>
              {!branchId ? (
                <span className="muted snmp-note">
                  Elija el gabinete o la sucursal para ver sus equipos con agente.
                </span>
              ) : (
                collectors.data?.length === 0 && (
                  <span className="muted snmp-note">
                    La sucursal no tiene equipos con agente: instale uno para consultar este dispositivo.
                  </span>
                )
              )}
            </div>
          </fieldset>
          {withApi && (
            <fieldset className="form-span snmp-fieldset">
              <legend>API REST del FortiGate (opcional)</legend>
              <div className="form-grid">
                <label className="field">
                  <span>Dirección</span>
                  <input
                    maxLength={200}
                    autoComplete="off"
                    value={form.apiUrl}
                    placeholder={apiSuggestion ?? 'https://IP:12443'}
                    onChange={(e) => setForm({ ...form, apiUrl: e.target.value })}
                  />
                </label>
                <span className="muted snmp-note">
                  {!form.apiUrl && apiSuggestion ? (
                    <button
                      type="button"
                      className="link-quiet"
                      onClick={() => setForm({ ...form, apiUrl: apiSuggestion })}
                    >
                      Usar {apiSuggestion}
                    </button>
                  ) : (
                    'Vacía: sin API (todo sigue por SNMP, ping y NetBIOS).'
                  )}
                </span>
                <label className="field">
                  <span>Token del usuario API</span>
                  <input
                    type="password"
                    autoComplete="new-password"
                    maxLength={128}
                    value={form.apiToken}
                    placeholder={device?.hasApiToken ? 'Guardado: vacío lo conserva' : 'Pegue el token'}
                    onChange={(e) => setForm({ ...form, apiToken: e.target.value })}
                  />
                </label>
                <label className="check snmp-note">
                  <input
                    type="checkbox"
                    checked={form.apiCert}
                    onChange={(e) => setForm({ ...form, apiCert: e.target.checked })}
                  />
                  Aceptar el certificado propio del equipo
                </label>
                <div className="form-span">
                  {device ? (
                    <ApiTestButton
                      deviceId={device.id}
                      disabled={!form.apiUrl.trim() && !device.apiUrl && !apiSuggestion}
                      input={{
                        apiUrl: form.apiUrl.trim() || apiSuggestion,
                        apiToken: form.apiToken.trim() || null,
                        apiAceptarCertificado: form.apiCert,
                      }}
                    />
                  ) : (
                    <span className="muted">Guarde el dispositivo para poder probar la API.</span>
                  )}
                  <p className="muted api-help">
                    Lee el DHCP (concesiones, rango y reservas) y los dispositivos que identifica el FortiGate. Usuario
                    REST API de solo lectura con la IP del recolector en Trusted Hosts. El token se guarda cifrado y no
                    se vuelve a mostrar.
                  </p>
                </div>
              </div>
            </fieldset>
          )}
        </>
      )}
      {!device && model && (
        <p className="muted form-span">
          Se crearán {model.portCount} puertos según el modelo ({model.portDescription ?? 'RJ45'}).
        </p>
      )}
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-67 asignar puerto a mano

/**
 * Fija a mano lo que hay en un puerto: un equipo (por ejemplo, una impresora sin agente; se ubica por su MAC) o que
 * es troncal. "Automático" lo devuelve a lo que detecte el recolector.
 */
export function PortAssignDialog({
  device,
  port,
  onClose,
}: {
  device: DeviceDetail;
  port: PortItem;
  onClose: () => void;
}) {
  const api = usePanelApi();
  const current = port.isManualAssignment ? (port.isTrunk ? 'troncal' : 'equipo') : 'auto';
  const [mode, setMode] = useState<'auto' | 'equipo' | 'troncal' | 'acceso'>(current);
  const [machineId, setMachineId] = useState(port.isManualAssignment && port.machineId ? String(port.machineId) : '');
  const machines = useQuery({
    queryKey: ['maquinas', 'sucursal', device.branchId],
    queryFn: () => api.machines({ sucursal: device.branchId, tamano: 200 }),
    enabled: mode === 'equipo',
  });
  const save = useNetworkSave(
    () =>
      api.red.assignPort(port.id, {
        maquinaId: mode === 'equipo' ? numberOrNull(machineId) : null,
        troncal: mode === 'troncal' ? true : mode === 'acceso' ? false : null,
      }),
    onClose,
  );

  return (
    <FormModal
      id="port-assign"
      title={`Asignar ${device.name} ${port.name ?? port.number}`}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <fieldset className="form-span choice-list">
        <legend>Qué hay en el puerto</legend>
        {(
          [
            ['auto', 'Automático: lo que detecte el recolector'],
            ['equipo', 'Un equipo en particular'],
            ['troncal', 'Troncal (enlace a otro equipo de red)'],
            ['acceso', 'Puerto de acceso (nunca troncal)'],
          ] as const
        ).map(([value, label]) => (
          <label key={value} className="choice">
            <input type="radio" name="modo" checked={mode === value} onChange={() => setMode(value)} />
            {label}
          </label>
        ))}
      </fieldset>
      {mode === 'equipo' && (
        <label className="field form-span">
          <span>Equipo *</span>
          <select required value={machineId} onChange={(e) => setMachineId(e.target.value)}>
            <option value="">Elija…</option>
            {(machines.data?.page.items ?? []).map((m) => (
              <option key={m.id} value={m.id}>
                {m.name}
                {m.ipAddress ? ` · ${m.ipAddress}` : ''}
              </option>
            ))}
          </select>
        </label>
      )}
      <p className="muted form-span">
        Lo asignado a mano no lo cambia el recolector; un equipo sin agente se reconoce por la MAC de su ficha.
      </p>
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-64 roseta

export function OutletForm({ outlet, onClose }: { outlet?: OutletItem; onClose: () => void }) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    codigo: outlet?.code ?? '',
    ubicacion: outlet?.location ?? '',
    sucursalId: outlet ? String(outlet.branchId) : '',
  });
  const save = useNetworkSave(
    () =>
      api.red.saveOutlet(outlet?.id ?? null, {
        codigo: form.codigo.trim(),
        ubicacion: textOrNull(form.ubicacion),
        sucursalId: Number(form.sucursalId),
      }),
    onClose,
  );
  return (
    <FormModal
      id="outlet-form"
      title={outlet ? `Editar roseta ${outlet.code}` : 'Agregar roseta'}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <label className="field">
        <span>Código *</span>
        <input
          required
          maxLength={30}
          value={form.codigo}
          placeholder="P-2-15"
          onChange={(e) => setForm({ ...form, codigo: e.target.value })}
          autoFocus
        />
      </label>
      <BranchSelect value={form.sucursalId} onChange={(v) => setForm({ ...form, sucursalId: v })} />
      <label className="field form-span">
        <span>Ubicación</span>
        <input
          maxLength={200}
          value={form.ubicacion}
          placeholder="Contabilidad, escritorio 3"
          onChange={(e) => setForm({ ...form, ubicacion: e.target.value })}
        />
      </label>
    </FormModal>
  );
}

// ------------------------------------------------------------------ puerto

export function PortForm({ port, onClose }: { port: PortItem; onClose: () => void }) {
  const api = usePanelApi();
  const [form, setForm] = useState({
    nombre: port.name ?? '',
    tipo: port.type,
    vlan: port.vlan ? String(port.vlan) : '',
    esHostVm: port.isVmHost,
  });
  const save = useNetworkSave(
    () =>
      api.red.savePort(port.id, {
        nombre: textOrNull(form.nombre),
        tipo: form.tipo,
        vlan: numberOrNull(form.vlan),
        esHostVm: form.esHostVm,
      }),
    onClose,
  );
  return (
    <FormModal
      id="port-form"
      title={`Puerto ${port.number}`}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
    >
      <label className="field">
        <span>Nombre</span>
        <input
          maxLength={50}
          value={form.nombre}
          placeholder="Gi1/0/12"
          onChange={(e) => setForm({ ...form, nombre: e.target.value })}
          autoFocus
        />
      </label>
      <label className="field">
        <span>Tipo</span>
        <select value={form.tipo} onChange={(e) => setForm({ ...form, tipo: e.target.value as PortType })}>
          {keysOf(PORT_TYPE_LABEL).map((t) => (
            <option key={t} value={t}>
              {PORT_TYPE_LABEL[t]}
            </option>
          ))}
        </select>
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
      <label className="check form-span">
        <input
          type="checkbox"
          checked={form.esHostVm}
          onChange={(e) => setForm({ ...form, esHostVm: e.target.checked })}
        />
        Servidor con máquinas virtuales (muchas MAC sin ser troncal)
      </label>
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-63 cable

/** Conectar un puerto con otro puerto o con una roseta de la misma sucursal. */
export function CableDialog({ device, port, onClose }: { device: DeviceDetail; port: PortItem; onClose: () => void }) {
  const api = usePanelApi();
  const [target, setTarget] = useState<'puerto' | 'roseta'>(device.type === 'patch_panel' ? 'roseta' : 'puerto');
  const [form, setForm] = useState({
    dispositivo: '',
    puerto: '',
    roseta: '',
    tipo: 'cat6' as CableType | '',
    color: '',
    etiqueta: '',
    notas: '',
  });
  const devices = useQuery({
    queryKey: ['red', 'dispositivos', { sucursal: device.branchId }],
    queryFn: () => api.red.devices({ sucursal: device.branchId }),
  });
  const other = useQuery({
    queryKey: ['red', 'dispositivo', Number(form.dispositivo)],
    queryFn: () => api.red.device(Number(form.dispositivo)),
    enabled: target === 'puerto' && form.dispositivo !== '',
  });
  const outlets = useQuery({
    queryKey: ['red', 'rosetas', device.branchId],
    queryFn: () => api.red.outlets(device.branchId),
    enabled: target === 'roseta',
  });

  const save = useNetworkSave(
    () =>
      api.red.createCable({
        puertoAId: port.id,
        puertoBId: target === 'puerto' ? numberOrNull(form.puerto) : null,
        puntoRedId: target === 'roseta' ? numberOrNull(form.roseta) : null,
        tipoCable: form.tipo || null,
        color: textOrNull(form.color),
        etiqueta: textOrNull(form.etiqueta),
        notas: textOrNull(form.notas),
      }),
    onClose,
  );

  return (
    <FormModal
      id="cable-form"
      title={`Conectar ${device.name} ${port.name ?? port.number}`}
      pending={save.isPending}
      error={save.error}
      onClose={onClose}
      onSubmit={() => save.mutate(undefined)}
      wide
    >
      <div className="segmented form-span" role="group" aria-label="Destino del cable">
        <button type="button" aria-pressed={target === 'puerto'} onClick={() => setTarget('puerto')}>
          A otro puerto
        </button>
        <button type="button" aria-pressed={target === 'roseta'} onClick={() => setTarget('roseta')}>
          A una roseta
        </button>
      </div>
      {target === 'puerto' ? (
        <>
          <label className="field">
            <span>Dispositivo *</span>
            <select
              required
              value={form.dispositivo}
              onChange={(e) => setForm({ ...form, dispositivo: e.target.value, puerto: '' })}
            >
              <option value="">Elija…</option>
              {(devices.data ?? [])
                .filter((d) => d.id !== device.id && d.portCount > 0)
                .map((d) => (
                  <option key={d.id} value={d.id}>
                    {d.name} · {DEVICE_TYPE_LABEL[d.type]}
                  </option>
                ))}
            </select>
          </label>
          <label className="field">
            <span>Puerto *</span>
            <select
              required
              value={form.puerto}
              disabled={!other.data}
              onChange={(e) => setForm({ ...form, puerto: e.target.value })}
            >
              <option value="">Elija…</option>
              {(other.data?.ports ?? []).map((p) => (
                <option key={p.id} value={p.id} disabled={p.cableId !== null && other.data?.type !== 'patch_panel'}>
                  {p.name ?? p.number}
                  {p.cableTo ? ` · ${p.cableTo}` : ''}
                </option>
              ))}
            </select>
          </label>
        </>
      ) : (
        <label className="field form-span">
          <span>Roseta *</span>
          <select required value={form.roseta} onChange={(e) => setForm({ ...form, roseta: e.target.value })}>
            <option value="">Elija…</option>
            {(outlets.data ?? []).map((o) => (
              <option key={o.id} value={o.id} disabled={o.cableId !== null}>
                {o.code}
                {o.location ? ` · ${o.location}` : ''}
                {o.portLabel ? ` (ya conectada a ${o.portLabel})` : ''}
              </option>
            ))}
          </select>
        </label>
      )}
      <label className="field">
        <span>Tipo de cable</span>
        <select value={form.tipo} onChange={(e) => setForm({ ...form, tipo: e.target.value as CableType | '' })}>
          <option value="">Sin indicar</option>
          {keysOf(CABLE_TYPE_LABEL).map((t) => (
            <option key={t} value={t}>
              {CABLE_TYPE_LABEL[t]}
            </option>
          ))}
        </select>
      </label>
      <label className="field">
        <span>Etiqueta y color</span>
        <span className="field-pair">
          <input
            maxLength={50}
            value={form.etiqueta}
            placeholder="PC-12"
            aria-label="Etiqueta"
            onChange={(e) => setForm({ ...form, etiqueta: e.target.value })}
          />
          <input
            maxLength={30}
            value={form.color}
            placeholder="Azul"
            aria-label="Color"
            onChange={(e) => setForm({ ...form, color: e.target.value })}
          />
        </span>
      </label>
      <label className="field form-span">
        <span>Notas</span>
        <input maxLength={500} value={form.notas} onChange={(e) => setForm({ ...form, notas: e.target.value })} />
      </label>
    </FormModal>
  );
}

// ------------------------------------------------------------------ W-65 ruta completa

const HOP_ICONS: Record<string, Parameters<typeof Icon>[0]['name']> = {
  roseta: 'plug',
  patch_panel: 'network',
  uplink: 'refresh',
};

export function PathDialog({
  from,
  title,
  onClose,
}: {
  from: { roseta?: number; puerto?: number };
  title: string;
  onClose: () => void;
}) {
  const api = usePanelApi();
  const path = useQuery({ queryKey: ['red', 'ruta', from], queryFn: () => api.red.path(from) });
  return (
    <Modal title={title} onClose={onClose} wide>
      {path.isPending ? (
        <Loading />
      ) : path.isError ? (
        <ErrorState error={path.error} />
      ) : path.data.length <= 1 ? (
        <p className="muted">
          No hay cableado documentado desde aquí. Conecte la roseta o el puerto en el editor de cableado.
        </p>
      ) : (
        <ol className="path">
          {path.data.map((hop, i) => (
            <li key={i} data-kind={hop.kind}>
              <span className="path-icon" aria-hidden="true">
                <Icon name={HOP_ICONS[hop.kind] ?? 'cpu'} size={16} />
              </span>
              <div>
                <strong className="mono">{hop.name}</strong>
                <span className="muted">
                  {hop.kind === 'uplink' ? 'Uplink' : (DEVICE_TYPE_LABEL[hop.kind as DeviceType] ?? hop.detail)}
                  {hop.cableLabel && ` · cable ${hop.cableLabel}`}
                </span>
              </div>
            </li>
          ))}
        </ol>
      )}
    </Modal>
  );
}

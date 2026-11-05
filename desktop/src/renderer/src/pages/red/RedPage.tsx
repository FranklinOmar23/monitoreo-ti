import { useState } from 'react';
import { PageHeader } from '@components/PageHeader';
import { Tabs } from '@components/Tabs';
import { DataCenterView } from './DataCenterView';
import { DeviceView } from './DeviceView';
import { NetworkHistory } from './NetworkHistory';
import { NetworkImport } from './NetworkImport';
import { DifferencesList, NetworkSearch, NetworkSummaryView, UnknownList } from './NetworkInsights';
import { CatalogList, DevicesList, OutletsList } from './NetworkLists';
import { IpAddressesView } from './IpAddressesView';
import { TopologyMap } from './TopologyMap';

const TABS = [
  { id: 'resumen', label: 'Resumen' },
  { id: 'datacenter', label: 'Data center' },
  { id: 'dispositivos', label: 'Dispositivos' },
  { id: 'rosetas', label: 'Rosetas' },
  { id: 'topologia', label: 'Topología' },
  { id: 'ips', label: 'Direcciones IP' },
  { id: 'desconocidos', label: 'Desconocidos' },
  { id: 'diferencias', label: 'Diferencias' },
  { id: 'buscar', label: 'Buscar MAC/IP' },
  { id: 'catalogo', label: 'Catálogo' },
  { id: 'historial', label: 'Historial' },
];

const IMPORT_TAB = { id: 'importar', label: 'Importar' };

interface RedPageProps {
  canEdit: boolean;
  onOpenEquipo: (id: number) => void;
  onOpenAlerta?: (id: number) => void;
  /** Pestaña inicial (las pruebas abren otra). */
  initialTab?: string;
  /** Abrir directamente un equipo de red (desde una vista 3D en su ventana). */
  initialDeviceId?: number;
}

/** Red y data center (Fase 4): inventario, recolector SNMP, correlación y cambios. */
export function RedPage({
  canEdit,
  onOpenEquipo,
  onOpenAlerta,
  initialTab = 'resumen',
  initialDeviceId,
}: RedPageProps) {
  const [tab, setTab] = useState(initialTab);
  const [deviceId, setDeviceId] = useState<number | null>(initialDeviceId ?? null);

  if (deviceId !== null)
    return (
      <DeviceView
        key={deviceId}
        deviceId={deviceId}
        canEdit={canEdit}
        onBack={() => setDeviceId(null)}
        onOpenEquipo={onOpenEquipo}
      />
    );

  return (
    <>
      <PageHeader title="Red y data center" subtitle="Salas, gabinetes, equipos de red y cableado" />
      <Tabs items={canEdit ? [...TABS, IMPORT_TAB] : TABS} activeId={tab} onChange={setTab} label="Secciones de red" />
      {tab === 'resumen' && <NetworkSummaryView onOpenDevice={setDeviceId} onOpenAlerta={onOpenAlerta} />}
      {tab === 'datacenter' && <DataCenterView canEdit={canEdit} onOpenDevice={setDeviceId} />}
      {tab === 'dispositivos' && <DevicesList canEdit={canEdit} onOpenDevice={setDeviceId} />}
      {tab === 'rosetas' && <OutletsList canEdit={canEdit} />}
      {tab === 'topologia' && <TopologyMap onOpenDevice={setDeviceId} onOpenEquipo={onOpenEquipo} />}
      {tab === 'ips' && <IpAddressesView canEdit={canEdit} onOpenEquipo={onOpenEquipo} />}
      {tab === 'desconocidos' && <UnknownList onOpenDevice={setDeviceId} />}
      {tab === 'diferencias' && <DifferencesList onOpenDevice={setDeviceId} onOpenEquipo={onOpenEquipo} />}
      {tab === 'buscar' && <NetworkSearch onOpenDevice={setDeviceId} onOpenEquipo={onOpenEquipo} />}
      {tab === 'catalogo' && <CatalogList canEdit={canEdit} />}
      {tab === 'historial' && <NetworkHistory />}
      {tab === 'importar' && canEdit && <NetworkImport />}
    </>
  );
}

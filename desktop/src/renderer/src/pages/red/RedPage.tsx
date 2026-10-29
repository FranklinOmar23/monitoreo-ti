import { useState } from 'react';
import { PageHeader } from '@components/PageHeader';
import { Tabs } from '@components/Tabs';
import { DataCenterView } from './DataCenterView';
import { DeviceView } from './DeviceView';
import { CatalogList, DevicesList, OutletsList } from './NetworkLists';

const TABS = [
  { id: 'datacenter', label: 'Data center' },
  { id: 'dispositivos', label: 'Dispositivos' },
  { id: 'rosetas', label: 'Rosetas' },
  { id: 'catalogo', label: 'Catálogo de modelos' },
];

interface RedPageProps {
  canEdit: boolean;
  onOpenEquipo: (id: number) => void;
}

/** Red y data center (Fase 4.1). El resumen, la topología y la correlación llegan con el recolector SNMP. */
export function RedPage({ canEdit, onOpenEquipo }: RedPageProps) {
  const [tab, setTab] = useState('datacenter');
  const [deviceId, setDeviceId] = useState<number | null>(null);

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
      <Tabs items={TABS} activeId={tab} onChange={setTab} label="Secciones de red" />
      {tab === 'datacenter' && <DataCenterView canEdit={canEdit} onOpenDevice={setDeviceId} />}
      {tab === 'dispositivos' && <DevicesList canEdit={canEdit} onOpenDevice={setDeviceId} />}
      {tab === 'rosetas' && <OutletsList canEdit={canEdit} />}
      {tab === 'catalogo' && <CatalogList canEdit={canEdit} />}
    </>
  );
}

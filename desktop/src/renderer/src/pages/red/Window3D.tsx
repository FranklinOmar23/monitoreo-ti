import { useEffect, useState } from 'react';
import type { RackDetail, RackItem } from '@monitoreoti/shared';
import { Loading } from '@components/AsyncState';
import type { View3DSnapshot } from '../../../../preload/bridge';
import { DataCenter3DScene } from './DataCenter3D';

/**
 * Vista 3D en su propia ventana (7.6, sección 12.2): para dejarla en otro monitor mientras se trabaja en el panel.
 * Muestra lo que manda la ventana principal y se actualiza sola; un clic en un equipo lo abre en el panel.
 */
export function Window3D() {
  const bridge = window.monitoreo?.view3d;
  // undefined = cargando; null = sin datos (fuera de la aplicación de escritorio o la ventana ya no tiene los suyos).
  const [snapshot, setSnapshot] = useState<View3DSnapshot | null | undefined>(bridge ? undefined : null);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);

  useEffect(() => {
    if (!bridge) return;
    const receive = (data: View3DSnapshot | null) => {
      setSnapshot(data);
      setUpdatedAt(new Date());
    };
    void bridge.data().then(receive, () => receive(null));
    return bridge.onData(receive);
  }, [bridge]);

  if (snapshot === undefined) return <Loading label="Cargando la vista 3D…" />;
  if (snapshot === null)
    return (
      <p className="view3d-empty muted" role="status">
        No hay datos para esta vista. Ciérrela y vuelva a abrirla desde el panel.
      </p>
    );

  return (
    <div className="view3d-window">
      <header className="view3d-head">
        <strong>{snapshot.title}</strong>
        <span className="muted">
          {snapshot.single ? 'Gabinete' : 'Sala'} · se actualiza sola
          {updatedAt && ` · ${updatedAt.toLocaleTimeString('es-DO', { hour: '2-digit', minute: '2-digit' })}`}
        </span>
      </header>
      <DataCenter3DScene
        racks={snapshot.racks as RackItem[]}
        details={snapshot.details as RackDetail[]}
        single={snapshot.single}
        onOpenDevice={(id) => void bridge?.openDevice(id)}
      />
    </div>
  );
}

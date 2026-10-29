import { useQuery } from '@tanstack/react-query';
import { EmptyState, ErrorState, Loading } from '@components/AsyncState';
import { PageHeader } from '@components/PageHeader';
import { usePanelApi } from '../data/PanelData';

interface QrPrintPageProps {
  machineIds: number[];
  onBack: () => void;
}

/**
 * W-14 Imprimir QR en lote: una hoja de etiquetas lista para imprimir (o guardar como PDF desde el diálogo
 * de impresión de Windows). Al imprimir solo sale la hoja, sin el resto del panel.
 */
export function QrPrintPage({ machineIds, onBack }: QrPrintPageProps) {
  const api = usePanelApi();
  const labels = useQuery({
    queryKey: ['qr-lote', machineIds],
    queryFn: () => api.qrLabels(machineIds),
    enabled: machineIds.length > 0,
  });

  return (
    <>
      <div className="no-print">
        <PageHeader
          title="Imprimir etiquetas QR"
          subtitle={`${machineIds.length} equipo(s) · al escanearla, la app abre la ficha del equipo`}
          actions={
            <>
              <button type="button" className="btn btn-outline" onClick={onBack}>
                Volver
              </button>
              <button
                type="button"
                className="btn btn-primary"
                disabled={!labels.data?.length}
                onClick={() => window.print()}
              >
                Imprimir
              </button>
            </>
          }
        />
      </div>
      {machineIds.length === 0 ? (
        <EmptyState title="No hay equipos seleccionados">
          Seleccione equipos en la lista y pulse "Imprimir QR".
        </EmptyState>
      ) : labels.isPending ? (
        <Loading />
      ) : labels.isError ? (
        <ErrorState error={labels.error} onRetry={() => void labels.refetch()} />
      ) : (
        <div className="qr-sheet">
          {labels.data.map((label) => (
            <figure key={label.id} className="qr-sticker">
              <img src={label.png} alt={`Código QR de ${label.nombre}`} />
              <figcaption>
                <strong className="mono">{label.nombre}</strong>
                <span>{label.sucursal}</span>
              </figcaption>
            </figure>
          ))}
        </div>
      )}
    </>
  );
}

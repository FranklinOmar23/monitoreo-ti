import { useIsFetching, useIsMutating } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

/** Retardo antes de mostrar la barra: lo que carga al instante no parpadea. */
const SHOW_AFTER_MS = 180;

/**
 * Barra de carga de 2px bajo la barra superior (decisión 71): aparece mientras algo carga por primera vez o se
 * guarda. Las actualizaciones periódicas en segundo plano (consultas que ya tienen datos) no la encienden.
 */
export function TopLoader() {
  const loading = useIsFetching({ predicate: (q) => q.state.status === 'pending' });
  const saving = useIsMutating();
  const busy = loading + saving > 0;
  const [visible, setVisible] = useState(false);

  useEffect(() => {
    if (!busy) return;
    const timer = setTimeout(() => setVisible(true), SHOW_AFTER_MS);
    // Al terminar (o al cambiar), se apaga: la próxima carga vuelve a esperar el retardo.
    return () => {
      clearTimeout(timer);
      setVisible(false);
    };
  }, [busy]);

  return <div className="top-loader" data-active={visible || undefined} aria-hidden="true" />;
}

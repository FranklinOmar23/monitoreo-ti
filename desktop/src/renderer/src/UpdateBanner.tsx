import { useEffect, useState } from 'react';

/**
 * Versión nueva del panel ya descargada (decisión 70): se ofrece reiniciar ahora; si no, se instala al salir.
 * Sin el puente con el proceso principal (pruebas, desarrollo) no aparece.
 */
export function UpdateBanner() {
  const [version, setVersion] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [installing, setInstalling] = useState(false);

  useEffect(() => {
    const updates = window.monitoreo?.updates;
    if (!updates) return;
    let active = true;
    void updates
      .status()
      .then((ready) => {
        if (active && ready) setVersion(ready);
      })
      .catch(() => undefined);
    const stop = updates.onReady((ready) => {
      setVersion(ready);
      setDismissed(false);
    });
    return () => {
      active = false;
      stop();
    };
  }, []);

  if (!version || dismissed) return null;
  return (
    <div className="update-banner" role="status">
      <span>
        <strong>Hay una versión nueva de Centinela ({version}).</strong> Se instala sola al salir, o ahora mismo en unos
        segundos.
      </span>
      <button type="button" className="btn btn-outline" onClick={() => setDismissed(true)}>
        Más tarde
      </button>
      <button
        type="button"
        className="btn btn-primary"
        disabled={installing}
        onClick={() => {
          setInstalling(true);
          void window.monitoreo?.updates.install().catch(() => setInstalling(false));
        }}
      >
        {installing ? 'Reiniciando…' : 'Reiniciar ahora'}
      </button>
    </div>
  );
}

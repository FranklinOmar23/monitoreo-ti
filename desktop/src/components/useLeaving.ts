import { useEffect, useState, type Key } from 'react';

export interface Shown<T> {
  item: T;
  /** Ya no está en la lista: se pinta con data-leaving (se desvanece) y se quita al terminar. */
  leaving: boolean;
}

const LEAVE_MS = 320;

function animates(): boolean {
  return typeof matchMedia === 'function' && !matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/** Junta la lista nueva con lo que se fue, cada uno en el lugar que tenía. */
function merge<T>(previous: Shown<T>[], items: readonly T[], keyOf: (item: T) => Key): Shown<T>[] {
  const current = new Set(items.map(keyOf));
  const result: Shown<T>[] = items.map((item) => ({ item, leaving: false }));
  let anchor: Key | null = null;
  for (const entry of previous) {
    const key = keyOf(entry.item);
    if (current.has(key)) {
      anchor = key;
      continue;
    }
    const at = anchor === null ? 0 : result.findIndex((e) => keyOf(e.item) === anchor) + 1;
    result.splice(at, 0, { item: entry.item, leaving: true });
    anchor = key;
  }
  return result;
}

/**
 * Lo que sale de una lista (una alerta que se resolvió) se queda un momento desvaneciéndose en lugar de desaparecer de
 * golpe. Sin animaciones (o en las pruebas) devuelve la lista tal cual.
 */
export function useLeaving<T>(items: readonly T[], keyOf: (item: T) => Key): Shown<T>[] {
  const [shown, setShown] = useState<Shown<T>[]>(() => items.map((item) => ({ item, leaving: false })));
  const [source, setSource] = useState(items);

  // Ajuste durante el render (patrón de React para derivar estado de las props). Se compara elemento por elemento:
  // `data ?? []` da un arreglo nuevo en cada render y no debe contar como cambio.
  if (source !== items && (source.length !== items.length || source.some((item, i) => item !== items[i]))) {
    setSource(items);
    setShown(animates() ? merge(shown, items, keyOf) : items.map((item) => ({ item, leaving: false })));
  }

  const hasLeaving = shown.some((e) => e.leaving);
  useEffect(() => {
    if (!hasLeaving) return;
    const timer = setTimeout(() => setShown((list) => list.filter((e) => !e.leaving)), LEAVE_MS);
    return () => clearTimeout(timer);
  }, [hasLeaving, shown]);

  return shown;
}

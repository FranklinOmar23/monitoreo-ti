/** Calidad de imagen del escritorio remoto: 0 = automática (según la latencia), o la calidad JPEG fija. */
export const QUALITIES = [
  { value: 0, label: 'Automática', hint: 'Se adapta a la conexión' },
  { value: 85, label: 'Alta', hint: 'Texto nítido · usa más datos' },
  { value: 60, label: 'Equilibrada', hint: 'Buena para la mayoría' },
  { value: 35, label: 'Rápida', hint: 'Para conexiones lentas o datos móviles' },
];

/** Atajos de teclado que el teclado local no puede mandar (Windows se los queda). */
export const SHORTCUTS: { label: string; keys: string[] }[] = [
  { label: 'Tecla Windows', keys: ['MetaLeft'] },
  { label: 'Alt+Tab', keys: ['AltLeft', 'Tab'] },
  { label: 'Ctrl+Esc', keys: ['ControlLeft', 'Escape'] },
  { label: 'Win+R (Ejecutar)', keys: ['MetaLeft', 'KeyR'] },
  { label: 'Win+D (Mostrar escritorio)', keys: ['MetaLeft', 'KeyD'] },
  { label: 'Win+L (Bloquear)', keys: ['MetaLeft', 'KeyL'] },
  { label: 'Alt+F4', keys: ['AltLeft', 'F4'] },
  { label: 'Impr Pant', keys: ['PrintScreen'] },
];

import { lazy, StrictMode, Suspense } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';
import './tecnico.css';
import './animations.css';
import { startDeepLinks } from './data/deepLinks';
import { applyBrandColor, cachedAppearance } from './theme/brand';

// El color de la última empresa vista, desde el primer cuadro (también en las ventanas de sesión y la vista 3D).
applyBrandColor(cachedAppearance()?.color ?? null);

// La terminal y el escritorio remoto abren en su propia ventana (#terminal/{sesión}, #escritorio/{sesión}); su código
// (xterm.js, el visor) se carga solo ahí.
const TerminalWindow = lazy(() => import('./pages/remoto/TerminalWindow').then((m) => ({ default: m.TerminalWindow })));
const DesktopWindow = lazy(() => import('./pages/remoto/DesktopWindow').then((m) => ({ default: m.DesktopWindow })));
// La vista 3D de una sala o un gabinete en su ventana (#vista3d/{clave}, 7.6).
const Window3D = lazy(() => import('./pages/red/Window3D').then((m) => ({ default: m.Window3D })));

const root = document.getElementById('root');
if (!root) throw new Error('No se encontró el elemento #root');

const hash = window.location.hash;

// Enlaces monitoreoti:// (sección 12.2): solo la ventana principal los atiende.
const isMainWindow = !/^#(terminal|escritorio|vista3d)\//.test(hash);
if (isMainWindow) startDeepLinks(window.monitoreo?.deepLinks);

createRoot(root).render(
  <StrictMode>
    {hash.startsWith('#terminal/') ? (
      <Suspense fallback={null}>
        <TerminalWindow />
      </Suspense>
    ) : hash.startsWith('#escritorio/') ? (
      <Suspense fallback={null}>
        <DesktopWindow />
      </Suspense>
    ) : hash.startsWith('#vista3d/') ? (
      <Suspense fallback={null}>
        <Window3D />
      </Suspense>
    ) : (
      <App />
    )}
  </StrictMode>,
);

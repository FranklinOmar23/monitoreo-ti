import { Fragment } from 'react';
import { Icon, type IconName } from './Icon';

export interface NavItem {
  id: string;
  label: string;
  icon: IconName;
  /** Conteo mostrado como insignia (alertas abiertas, tickets...). */
  badge?: number;
  /** Grupo del ítem (Monitoreo, Infraestructura...): su encabezado sale antes del primero de cada grupo. */
  section?: string;
}

interface SidebarProps {
  items: readonly NavItem[];
  activeId: string;
  onNavigate: (id: string) => void;
  /** Empresa de la sesión; el detalle (sucursales, equipos) se muestra cuando se conoce. */
  empresa: { nombre: string; detalle?: string };
  /** Logo de la empresa (data URL, W-40); sin él, la marca de Centinela. */
  logo?: string | null;
}

export function Sidebar({ items, activeId, onNavigate, empresa, logo }: SidebarProps) {
  return (
    <aside className="sidebar">
      <div className="sidebar-brand">
        {logo ? (
          <img className="brand-logo" src={logo} alt={empresa.nombre} />
        ) : (
          <>
            <span className="brand-mark">
              <Icon name="logo" size={20} />
            </span>
            <span className="brand-name">Centinela</span>
          </>
        )}
      </div>

      <nav className="sidebar-nav" aria-label="Navegación principal">
        {items.map((item, i) => (
          <Fragment key={item.id}>
            {item.section && item.section !== items[i - 1]?.section && (
              <span className="nav-section" aria-hidden="true">
                {item.section}
              </span>
            )}
            <button
              type="button"
              className="nav-item"
              aria-current={item.id === activeId ? 'page' : undefined}
              onClick={() => onNavigate(item.id)}
            >
              <Icon name={item.icon} />
              <span className="nav-label">{item.label}</span>
              {item.badge !== undefined && <span className="nav-badge mono">{item.badge}</span>}
            </button>
          </Fragment>
        ))}
      </nav>

      <div className="sidebar-company">
        <span className="eyebrow">Empresa</span>
        <strong>{empresa.nombre}</strong>
        {empresa.detalle && <span className="sidebar-company-meta">{empresa.detalle}</span>}
      </div>
    </aside>
  );
}

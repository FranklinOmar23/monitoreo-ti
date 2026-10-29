import { useEffect, useRef, useState } from 'react';
import { Icon } from './Icon';

export interface ProfileMenuItem {
  label: string;
  onSelect: () => void;
}

interface TopbarProps {
  search: string;
  onSearchChange: (value: string) => void;
  user: { nombre: string; rol: string };
  hasUnreadAlerts: boolean;
  menu: readonly ProfileMenuItem[];
}

/** "Luis Peña" → "LP". */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const letters = parts.length > 1 ? [parts[0]![0], parts.at(-1)![0]] : [parts[0]?.[0], parts[0]?.[1]];
  return letters.filter(Boolean).join('').toUpperCase();
}

/** Barra superior: buscador global (nombre, QR, IP), notificaciones y perfil. */
export function Topbar({ search, onSearchChange, user, hasUnreadAlerts, menu }: TopbarProps) {
  const [open, setOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  // Cerrar el menú al hacer clic fuera o con Escape.
  useEffect(() => {
    if (!open) return;
    const onClick = (event: MouseEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('mousedown', onClick);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClick);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <header className="topbar">
      <label className="search search-global">
        <Icon name="search" />
        <input
          type="search"
          value={search}
          placeholder="Buscar equipo por nombre, QR o IP"
          aria-label="Buscar equipo por nombre, QR o IP"
          onChange={(event) => onSearchChange(event.target.value)}
        />
        <kbd className="mono">Ctrl K</kbd>
      </label>

      <div className="topbar-right">
        <button
          type="button"
          className="icon-button"
          aria-label={hasUnreadAlerts ? 'Notificaciones (hay nuevas)' : 'Notificaciones'}
        >
          <Icon name="bell" />
          {hasUnreadAlerts && <span className="notif-dot" aria-hidden="true" />}
        </button>
        <div className="profile-wrap" ref={menuRef}>
          <button
            type="button"
            className="profile"
            aria-haspopup="menu"
            aria-expanded={open}
            onClick={() => setOpen((value) => !value)}
          >
            <span className="avatar" aria-hidden="true">
              {initials(user.nombre)}
            </span>
            <span className="profile-text">
              <strong>{user.nombre}</strong>
              <span>{user.rol}</span>
            </span>
            <Icon name="chevronDown" size={14} />
          </button>
          {open && (
            <div className="profile-menu" role="menu">
              {menu.map((item) => (
                <button
                  key={item.label}
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    item.onSelect();
                  }}
                >
                  {item.label}
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

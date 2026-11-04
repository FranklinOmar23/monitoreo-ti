import { useEffect, useRef, useState } from 'react';
import { Icon } from './Icon';

export interface ProfileMenuItem {
  label: string;
  onSelect: () => void;
}

/** Un aviso de la campana: una alerta abierta. */
export interface NotificationItem {
  id: number;
  level: 'advertencia' | 'critico';
  message: string;
  where: string | null;
  at: string;
}

interface TopbarProps {
  search: string;
  onSearchChange: (value: string) => void;
  user: { nombre: string; rol: string };
  hasUnreadAlerts: boolean;
  menu: readonly ProfileMenuItem[];
  /** Alertas abiertas más recientes para la campana; sin la lista, la campana no abre nada. */
  notifications?: readonly NotificationItem[];
  onOpenNotification?: (id: number) => void;
  onSeeAllNotifications?: () => void;
}

/** "Luis Peña" → "LP". */
export function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  const letters = parts.length > 1 ? [parts[0]![0], parts.at(-1)![0]] : [parts[0]?.[0], parts[0]?.[1]];
  return letters.filter(Boolean).join('').toUpperCase();
}

/** Barra superior: buscador global (nombre, QR, IP), notificaciones y perfil. */
export function Topbar({
  search,
  onSearchChange,
  user,
  hasUnreadAlerts,
  menu,
  notifications,
  onOpenNotification,
  onSeeAllNotifications,
}: TopbarProps) {
  const [open, setOpen] = useState(false);
  const [bellOpen, setBellOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const bellRef = useRef<HTMLDivElement>(null);

  // Cerrar el menú y la campana al hacer clic fuera o con Escape.
  useEffect(() => {
    if (!open && !bellOpen) return;
    const onClick = (event: MouseEvent) => {
      if (!menuRef.current?.contains(event.target as Node)) setOpen(false);
      if (!bellRef.current?.contains(event.target as Node)) setBellOpen(false);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setOpen(false);
        setBellOpen(false);
      }
    };
    document.addEventListener('mousedown', onClick);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onClick);
      document.removeEventListener('keydown', onKey);
    };
  }, [open, bellOpen]);

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
        <div className="profile-wrap" ref={bellRef}>
          <button
            type="button"
            className="icon-button"
            aria-label={hasUnreadAlerts ? 'Notificaciones (hay nuevas)' : 'Notificaciones'}
            aria-haspopup="dialog"
            aria-expanded={bellOpen}
            onClick={() => notifications && setBellOpen((value) => !value)}
          >
            <Icon name="bell" />
            {hasUnreadAlerts && <span className="notif-dot" aria-hidden="true" />}
          </button>
          {bellOpen && notifications && (
            <div className="notif-panel" role="dialog" aria-label="Notificaciones">
              <strong className="notif-title">Alertas abiertas</strong>
              {notifications.length === 0 ? (
                <p className="muted">No hay alertas abiertas.</p>
              ) : (
                <ul>
                  {notifications.map((n) => (
                    <li key={n.id} data-level={n.level}>
                      <button
                        type="button"
                        onClick={() => {
                          setBellOpen(false);
                          onOpenNotification?.(n.id);
                        }}
                      >
                        <span>{n.message}</span>
                        <span className="muted">{n.where ?? 'Sin equipo'}</span>
                      </button>
                    </li>
                  ))}
                </ul>
              )}
              {onSeeAllNotifications && (
                <button
                  type="button"
                  className="link-quiet notif-all"
                  onClick={() => {
                    setBellOpen(false);
                    onSeeAllNotifications();
                  }}
                >
                  Ver todas las alertas
                </button>
              )}
            </div>
          )}
        </div>
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

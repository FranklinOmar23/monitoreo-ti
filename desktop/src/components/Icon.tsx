const PATHS = {
  dashboard: 'M4 4h6v7H4zM14 4h6v4h-6zM14 12h6v8h-6zM4 15h6v5H4z',
  monitor: 'M3 5h18v11H3zM8 20h8M12 16v4',
  building: 'M5 21V4h9v17M14 9h5v12M9 8h1M9 12h1M9 16h1M17 13h0M3 21h18',
  bell: 'M6 16V11a6 6 0 0 1 12 0v5l2 2H4zM10 21h4',
  ticket: 'M3 8a2 2 0 0 0 0 8v2h18v-2a2 2 0 0 1 0-8V6H3zM14 6v12',
  remote: 'M3 5h18v11H3zM8 20h8M12 16v4M9 10l3-3 3 3',
  chart: 'M4 4v16h16M8 16v-5M12 16V8M16 16v-3',
  download: 'M12 4v11M7 11l5 5 5-5M5 20h14',
  users:
    'M9 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7zM2.5 20a6.5 6.5 0 0 1 13 0M16 4.5a3.5 3.5 0 0 1 0 6.5M18 14a6 6 0 0 1 3.5 6',
  gear: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM19 12a7 7 0 0 0-.1-1.2l2-1.5-2-3.5-2.3 1a7 7 0 0 0-2-1.2L14.2 3h-4l-.4 2.6a7 7 0 0 0-2 1.2l-2.3-1-2 3.5 2 1.5a7 7 0 0 0 0 2.4l-2 1.5 2 3.5 2.3-1a7 7 0 0 0 2 1.2l.4 2.6h4l.4-2.6a7 7 0 0 0 2-1.2l2.3 1 2-3.5-2-1.5c.1-.4.1-.8.1-1.2z',
  search: 'M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14zM16 16l5 5',
  chevronDown: 'M6 9l6 6 6-6',
  dots: 'M5 12h.01M12 12h.01M19 12h.01',
  pin: 'M12 21s-6-5.3-6-10a6 6 0 0 1 12 0c0 4.7-6 10-6 10zM12 12.5a2 2 0 1 0 0-4 2 2 0 0 0 0 4z',
  user: 'M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0',
  clock: 'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3 2',
  disk: 'M3 14h18v5H3zM3 14l3-8h12l3 8M17 16.5h.01',
  database:
    'M5 6c0-1.7 3.1-3 7-3s7 1.3 7 3-3.1 3-7 3-7-1.3-7-3zM5 6v6c0 1.7 3.1 3 7 3s7-1.3 7-3V6M5 12v6c0 1.7 3.1 3 7 3s7-1.3 7-3v-6',
  thermometer: 'M10 14.5V5a2 2 0 0 1 4 0v9.5a4 4 0 1 1-4 0z',
  power: 'M12 3v8M6.3 7a8 8 0 1 0 11.4 0',
  wrench: 'M14.5 6a4 4 0 0 0-5 5L3 17.5 6.5 21l6.5-6.5a4 4 0 0 0 5-5l-2.5 2.5-2.5-.5-.5-2.5z',
  cpu: 'M7 7h10v10H7zM10 10h4v4h-4zM9 3v4M15 3v4M9 17v4M15 17v4M3 9h4M3 15h4M17 9h4M17 15h4',
  memory: 'M3 8h18v8H3zM7 16v3M11 16v3M15 16v3M19 16v3M7 11v2M11 11v2M15 11v2',
  bolt: 'M13 3L5 13h6l-1 8 8-10h-6z',
  fan: 'M12 14a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM12 10c0-4 1-7 4-7 2 0 3 2 1 4l-5 3M14 12c4 0 7 1 7 4 0 2-2 3-4 1l-3-5M12 14c0 4-1 7-4 7-2 0-3-2-1-4l5-3M10 12c-4 0-7-1-7-4 0-2 2-3 4-1l3 5',
  shield: 'M12 3l8 3v6c0 4.5-3.4 8.2-8 9-4.6-.8-8-4.5-8-9V6zM8.5 12l2.5 2.5 4.5-5',
  printer: 'M7 9V3h10v6M7 17H4v-7h16v7h-3M7 14h10v7H7z',
  network: 'M12 4v5M5 20v-4h14v4M12 9v7M8 9h8M3 20h4M17 20h4',
  laptop: 'M5 6h14v10H5zM2 19h20',
  qr: 'M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h2v2h-2zM18 14h2v2M14 18h2v2M18 18h2v2',
  refresh: 'M20 11a8 8 0 0 0-14.5-3.5M4 4v4h4M4 13a8 8 0 0 0 14.5 3.5M20 20v-4h-4',
  terminal: 'M4 5h16v14H4zM8 10l3 2-3 2M13 15h3',
  folder: 'M3 6h6l2 2h10v11H3z',
  plug: 'M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4',
  plus: 'M12 5v14M5 12h14',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  logo: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10zM12 11a1 1 0 1 0 0 2 1 1 0 0 0 0-2z',
} as const;

export type IconName = keyof typeof PATHS;

interface IconProps {
  name: IconName;
  size?: number;
}

/** Iconos de línea (24×24, trazo 1.7). Decorativos: el texto vecino da el significado. */
export function Icon({ name, size = 18 }: IconProps) {
  return (
    <svg
      className="icon"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={PATHS[name]} />
    </svg>
  );
}

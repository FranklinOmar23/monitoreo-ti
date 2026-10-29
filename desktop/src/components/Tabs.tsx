export interface TabItem {
  id: string;
  label: string;
  /** Conteo de pendientes mostrado como insignia. */
  count?: number;
}

interface TabsProps {
  items: readonly TabItem[];
  activeId: string;
  onChange: (id: string) => void;
  label: string;
}

export function Tabs({ items, activeId, onChange, label }: TabsProps) {
  return (
    <div className="tabs" role="tablist" aria-label={label}>
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          role="tab"
          aria-selected={item.id === activeId}
          className="tab"
          onClick={() => onChange(item.id)}
        >
          {item.label}
          {item.count ? <span className="tab-count">{item.count}</span> : null}
        </button>
      ))}
    </div>
  );
}

import { Icon } from './Icon';

interface FilterSelectProps {
  label: string;
  value: string;
  options: readonly { value: string; label: string }[];
  /** Opción vacía ("Todos"); sin ella el filtro siempre tiene un valor. */
  allLabel?: string;
  onChange: (value: string) => void;
}

/** Filtro de lista, con la opción "Todos" vacía si se indica. */
export function FilterSelect({ label, value, options, allLabel, onChange }: FilterSelectProps) {
  return (
    <label className="filter-select">
      <span>{label}:</span>
      <select value={value} onChange={(event) => onChange(event.target.value)}>
        {allLabel !== undefined && <option value="">{allLabel}</option>}
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
      <Icon name="chevronDown" size={14} />
    </label>
  );
}

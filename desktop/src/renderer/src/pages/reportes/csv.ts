/** Exportar reportes a CSV (W-35) sin librerías: Excel lo abre con acentos gracias a la marca UTF-8 del principio. */

export type CsvCell = string | number | boolean | null | undefined;

export interface CsvTable {
  columns: readonly string[];
  rows: readonly (readonly CsvCell[])[];
}

function cell(value: CsvCell): string {
  if (value === null || value === undefined) return '';
  const text = typeof value === 'boolean' ? (value ? 'Sí' : 'No') : String(value);
  // Una celda que empieza con = + - @ la interpreta Excel como fórmula: se antepone un apóstrofo.
  const safe = /^[=+\-@\t\r]/.test(text) && typeof value !== 'number' ? `'${text}` : text;
  return /[",\r\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
}

export function toCsv({ columns, rows }: CsvTable): string {
  return '﻿' + [columns, ...rows].map((row) => row.map(cell).join(',')).join('\r\n') + '\r\n';
}

/** Nombre de archivo seguro: "inventario-2026-11-01.csv". */
export function csvFileName(report: string, date: Date = new Date()): string {
  const day = date.toISOString().slice(0, 10);
  return `${report}-${day}.csv`;
}

/** Descarga el archivo: Electron muestra el diálogo "Guardar como". */
export function downloadCsv(table: CsvTable, fileName: string): void {
  const url = URL.createObjectURL(new Blob([toCsv(table)], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

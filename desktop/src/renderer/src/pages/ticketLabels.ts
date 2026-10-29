import type { TicketPriority, TicketStatus } from '@monitoreoti/shared';

export const PRIORITY_LABEL: Record<TicketPriority, string> = {
  baja: 'Baja',
  media: 'Media',
  alta: 'Alta',
  urgente: 'Urgente',
};

export const TICKET_STATUS_LABEL: Record<TicketStatus, string> = {
  abierto: 'Abierto',
  en_proceso: 'En proceso',
  en_espera: 'En espera',
  resuelto: 'Resuelto',
  cerrado: 'Cerrado',
};

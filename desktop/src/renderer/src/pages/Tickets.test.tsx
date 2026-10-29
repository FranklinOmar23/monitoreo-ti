import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TicketDetail } from '@monitoreoti/shared';
import { fakePanelApi, renderWithPanel, ticketItem, ticketList } from '../test/fakes';
import { NewTicketDialog } from './NewTicketDialog';
import { TicketsPage } from './TicketsPage';

afterEach(cleanup);

const now = new Date().toISOString();

const detail: TicketDetail = {
  ...ticketItem(),
  description: 'El disco tiene sectores dañados.',
  machineStatus: 'critico',
  branchId: 1,
  alertId: 41,
  alertMessage: 'Disco con sectores dañados',
  alertStatus: 'en_proceso',
  createdById: 3,
  comments: [{ id: 1, userName: 'Luis Gómez', text: 'Pedí el disco nuevo.', at: now }],
  attachments: [],
  history: [{ at: now, action: 'Ticket creado', userName: 'Ana Pérez', detail: null }],
};

const handlers = { onOpenEquipo: vi.fn(), onOpenAlert: vi.fn() };

describe('Tickets (W-24 a W-26)', () => {
  it('el tablero agrupa por estado y mueve un ticket sin arrastrar', async () => {
    const setTicketStatus = vi.fn(() => Promise.resolve({ id: 7 }));
    renderWithPanel(
      <TicketsPage canManage {...handlers} />,
      fakePanelApi({
        tickets: () =>
          Promise.resolve(
            ticketList([ticketItem(), ticketItem({ id: 8, title: 'Impresora sin tóner', status: 'en_espera' })]),
          ),
        setTicketStatus,
      }),
    );

    const open = await screen.findByRole('region', { name: 'Abierto' });
    expect(open.textContent).toContain('Cambiar disco de RRHH-02');
    expect(screen.getByRole('region', { name: 'En espera' }).textContent).toContain('Impresora sin tóner');
    expect(screen.getByText('2 sin asignar')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Mover el ticket 7'), { target: { value: 'en_proceso' } });
    await waitFor(() => expect(setTicketStatus).toHaveBeenCalledWith(7, 'en_proceso'));
  });

  it('sin permiso de gestión no se puede mover', async () => {
    renderWithPanel(
      <TicketsPage canManage={false} {...handlers} />,
      fakePanelApi({ tickets: () => Promise.resolve(ticketList([ticketItem()])) }),
    );

    await screen.findByText('Cambiar disco de RRHH-02');
    expect(screen.queryByLabelText('Mover el ticket 7')).toBeNull();
  });

  it('el detalle muestra la conversación, la alerta de origen y permite comentar', async () => {
    const commentTicket = vi.fn(() => Promise.resolve({ id: 2 }));
    renderWithPanel(
      <TicketsPage canManage initialTicketId={7} {...handlers} />,
      fakePanelApi({ ticket: () => Promise.resolve(detail), commentTicket }),
    );

    expect(await screen.findByText('Pedí el disco nuevo.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /Alerta #41/ }));
    expect(handlers.onOpenAlert).toHaveBeenCalledWith(41);

    fireEvent.change(screen.getByLabelText('Agregar comentario'), { target: { value: 'Disco cambiado.' } });
    fireEvent.click(screen.getByRole('button', { name: 'Comentar' }));
    await waitFor(() => expect(commentTicket).toHaveBeenCalledWith(7, 'Disco cambiado.'));
  });

  it('crear desde una alerta manda la alerta, no el equipo', async () => {
    const createTicket = vi.fn(() => Promise.resolve({ id: 9 }));
    const onCreated = vi.fn();
    renderWithPanel(
      <NewTicketDialog
        alertId={41}
        machineId={1}
        defaultTitle="Disco con sectores dañados"
        defaultPriority="alta"
        canAssign={false}
        onClose={vi.fn()}
        onCreated={onCreated}
      />,
      fakePanelApi({ createTicket }),
    );

    expect((screen.getByLabelText('Título *') as HTMLInputElement).value).toBe('Disco con sectores dañados');
    expect(screen.queryByLabelText('Asignar a')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Crear ticket' }));

    await waitFor(() =>
      expect(createTicket).toHaveBeenCalledWith({
        titulo: 'Disco con sectores dañados',
        descripcion: null,
        prioridad: 'alta',
        maquinaId: null,
        alertaId: 41,
        asignadoA: null,
      }),
    );
    expect(onCreated).toHaveBeenCalledWith(9);
  });
});

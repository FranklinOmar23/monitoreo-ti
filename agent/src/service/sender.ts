import type { Envelope } from '@monitoreoti/shared';
import type { Outbox } from './outbox';

export interface SenderTransport {
  /** Envía y espera el ack del servidor; rechaza si no hay conexión o no confirma. */
  request(envelope: Envelope): Promise<void>;
}

/**
 * Vacía la cola local hacia el servidor en orden. Un mensaje se borra solo tras su ack; si falla uno,
 * se detiene y se reintenta en la próxima señal (reconexión o temporizador), sin saltarse el orden.
 */
export class Sender {
  private draining = false;
  private again = false;

  constructor(
    private readonly outbox: Outbox,
    private readonly transport: SenderTransport,
    private readonly log: (message: string, extra?: Record<string, unknown>) => void = () => undefined,
    private readonly batchSize = 50,
  ) {}

  /** Encola y trata de enviar de inmediato. */
  submit(envelope: Envelope): void {
    this.outbox.enqueue(envelope);
    void this.drain();
  }

  /** Un solo vaciado a la vez; si llegan señales durante uno en curso, se repite al terminar. */
  async drain(): Promise<void> {
    if (this.draining) {
      this.again = true;
      return;
    }
    this.draining = true;

    try {
      do {
        this.again = false;
        await this.drainOnce();
      } while (this.again);
    } finally {
      this.draining = false;
    }
  }

  private async drainOnce(): Promise<void> {
    for (;;) {
      const batch = this.outbox.peek(this.batchSize);
      if (batch.length === 0) return;

      for (const envelope of batch) {
        try {
          await this.transport.request(envelope);
        } catch (error) {
          this.log('Envío pendiente; se reintentará', { tipo: envelope.tipo, error: String(error) });
          return;
        }
        this.outbox.remove(envelope.id);
      }
    }
  }
}

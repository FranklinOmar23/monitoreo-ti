import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import type { Envelope } from '@monitoreoti/shared';

/** Máximo de mensajes guardados sin conexión; al pasarlo se descartan primero las métricas más viejas. */
export const OUTBOX_LIMIT = 10_000;

/**
 * Cola local en SQLite (node:sqlite, incluido en Node: sin módulos nativos que compilar).
 * Todo mensaje se guarda aquí antes de enviarse y solo se borra cuando el servidor lo confirma (ack):
 * si no hay red, o el servicio se reinicia a medias, nada se pierde.
 */
export class Outbox {
  private readonly db: DatabaseSync;

  constructor(
    path: string,
    private readonly limit = OUTBOX_LIMIT,
  ) {
    if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true });
    this.db = new DatabaseSync(path);
    this.db.exec(`
      PRAGMA journal_mode = WAL;
      CREATE TABLE IF NOT EXISTS outbox (
        seq     INTEGER PRIMARY KEY AUTOINCREMENT,
        id      TEXT NOT NULL UNIQUE,
        tipo    TEXT NOT NULL,
        payload TEXT NOT NULL
      );
    `);
  }

  enqueue(envelope: Envelope): void {
    // OR IGNORE: encolar dos veces el mismo id no duplica el mensaje.
    this.db
      .prepare('INSERT OR IGNORE INTO outbox (id, tipo, payload) VALUES (?, ?, ?)')
      .run(envelope.id, envelope.tipo, JSON.stringify(envelope));
    this.enforceLimit();
  }

  /** Los mensajes más antiguos primero, para conservar el orden en que ocurrieron. */
  peek(limit: number): Envelope[] {
    const rows = this.db.prepare('SELECT payload FROM outbox ORDER BY seq LIMIT ?').all(limit) as { payload: string }[];
    return rows.map((row) => JSON.parse(row.payload) as Envelope);
  }

  remove(id: string): void {
    this.db.prepare('DELETE FROM outbox WHERE id = ?').run(id);
  }

  count(): number {
    return (this.db.prepare('SELECT COUNT(*) AS n FROM outbox').get() as { n: number }).n;
  }

  close(): void {
    this.db.close();
  }

  private enforceLimit(): void {
    const excess = this.count() - this.limit;
    if (excess <= 0) return;

    // Las métricas viejas son lo menos valioso; los inventarios y eventos se conservan mientras se pueda.
    this.db
      .prepare(
        `DELETE FROM outbox WHERE seq IN (
           SELECT seq FROM outbox ORDER BY (tipo = 'metricas') DESC, seq LIMIT ?
         )`,
      )
      .run(excess);
  }
}

import type { Envelope } from '@monitoreoti/shared';
import { describe, expect, it, vi } from 'vitest';
import { IpSweeper, parseSweepRequest } from './ipsweep';

describe('barrido de IP', () => {
  it('lee el pedido del servidor y descarta lo inválido', () => {
    expect(parseSweepRequest({ subred_id: 3, ips: ['10.0.0.1', 'x', 5], arp_dispositivo_id: 7 })).toEqual({
      subnetId: 3,
      ips: ['10.0.0.1'],
      arpDeviceId: 7,
    });
    expect(parseSweepRequest({ subred_id: 3, ips: [] })).toBeNull();
    expect(parseSweepRequest({ ips: ['10.0.0.1'] })).toBeNull();
    expect(parseSweepRequest({ subred_id: 3, ips: Array.from({ length: 1025 }, () => '10.0.0.1') })).toBeNull();
  });

  it('hace ping, suma la ARP del firewall y manda el resultado', async () => {
    const sent: Envelope[] = [];
    const sweep = vi.fn(() =>
      Promise.resolve({
        ok: true,
        responden: ['10.0.0.1'],
        arp: [{ ip: '10.0.0.5', mac: 'AA:BB:CC:00:00:05' }],
        nombres: [{ ip: '10.0.0.1', nombre: 'CONTA-PC03', origen: 'netbios' as const }],
      }),
    );
    const arpOf = vi.fn(() => Promise.resolve([{ ip: '10.0.0.9', mac: 'AA:BB:CC:00:00:09' }]));
    const sweeper = new IpSweeper({ sweep, arpOf, submit: (e) => sent.push(e), log: vi.fn() });

    await sweeper.handle({ subred_id: 3, ips: ['10.0.0.1', '10.0.0.5', '10.0.0.9'], arp_dispositivo_id: 7 });

    expect(sweep).toHaveBeenCalledWith(['10.0.0.1', '10.0.0.5', '10.0.0.9']);
    expect(arpOf).toHaveBeenCalledWith(7);
    expect(sent[0]!.tipo).toBe('barrido_ip_resultado');
    expect(sent[0]!.datos).toEqual({
      subredId: 3,
      ok: true,
      error: undefined,
      responden: ['10.0.0.1'],
      arp: [
        { ip: '10.0.0.5', mac: 'AA:BB:CC:00:00:05' },
        { ip: '10.0.0.9', mac: 'AA:BB:CC:00:00:09' },
      ],
      nombres: [{ ip: '10.0.0.1', nombre: 'CONTA-PC03', origen: 'netbios' }],
    });
  });

  it('si el barrido falla lo informa y no consulta la ARP', async () => {
    const sent: Envelope[] = [];
    const arpOf = vi.fn(() => Promise.resolve(null));
    const sweeper = new IpSweeper({
      sweep: () => Promise.resolve({ ok: false, error: 'El barrido de IP tardó demasiado.' }),
      arpOf,
      submit: (e) => sent.push(e),
      log: vi.fn(),
    });

    await sweeper.handle({ subred_id: 4, ips: ['10.0.0.1'], arp_dispositivo_id: 7 });

    expect(arpOf).not.toHaveBeenCalled();
    expect(sent[0]!.datos).toMatchObject({ subredId: 4, ok: false, error: 'El barrido de IP tardó demasiado.' });
  });

  it('hace los barridos de a uno', async () => {
    const order: string[] = [];
    let release!: () => void;
    const first = new Promise<void>((resolve) => (release = resolve));
    const sweeper = new IpSweeper({
      sweep: async (ips) => {
        order.push(`inicio ${ips[0]}`);
        if (ips[0] === '10.0.0.1') await first;
        order.push(`fin ${ips[0]}`);
        return { ok: true, responden: [] };
      },
      arpOf: () => Promise.resolve(null),
      submit: vi.fn(),
      log: vi.fn(),
    });

    const a = sweeper.handle({ subred_id: 1, ips: ['10.0.0.1'] });
    const b = sweeper.handle({ subred_id: 2, ips: ['10.0.1.1'] });
    await Promise.resolve();
    release();
    await Promise.all([a, b]);

    expect(order).toEqual(['inicio 10.0.0.1', 'fin 10.0.0.1', 'inicio 10.0.1.1', 'fin 10.0.1.1']);
  });
});

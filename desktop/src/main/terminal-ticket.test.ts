import { describe, expect, it } from 'vitest';
import { parseTerminalTicket } from './terminal-ticket';

const valid = { sessionId: 12, relayPath: '/relay/12', token: 'a'.repeat(43), machineName: 'CONTA-03' };

describe('ticket de la ventana de terminal', () => {
  it('acepta un ticket bien formado', () => {
    expect(parseTerminalTicket(valid)).toEqual({ ...valid, kind: 'terminal' });
    expect(parseTerminalTicket({ ...valid, kind: 'escritorio' })).toMatchObject({ kind: 'escritorio' });
    expect(parseTerminalTicket({ ...valid, kind: 'otra' })).toBeNull();
    expect(parseTerminalTicket({ ...valid, machineId: 7 })).toMatchObject({ machineId: 7 });
    expect(parseTerminalTicket({ ...valid, machineId: -1 })).toBeNull();
  });

  it('rechaza un relay de otra sesión o fuera del servidor y tokens raros', () => {
    expect(parseTerminalTicket({ ...valid, relayPath: '/relay/13' })).toBeNull();
    expect(parseTerminalTicket({ ...valid, relayPath: 'https://otro/relay/12' })).toBeNull();
    expect(parseTerminalTicket({ ...valid, token: 'corto' })).toBeNull();
    expect(parseTerminalTicket({ ...valid, token: `${'a'.repeat(30)}"><script>` })).toBeNull();
    expect(parseTerminalTicket({ ...valid, machineName: '' })).toBeNull();
    expect(parseTerminalTicket(null)).toBeNull();
  });
});

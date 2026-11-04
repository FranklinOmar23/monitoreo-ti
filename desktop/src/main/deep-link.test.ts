import { describe, expect, it } from 'vitest';
import { findDeepLink, parseDeepLink } from './deep-link';

describe('enlaces monitoreoti://', () => {
  it('reconoce los tres enlaces', () => {
    expect(parseDeepLink('monitoreoti://maquina/42')).toEqual({ kind: 'maquina', id: 42 });
    expect(parseDeepLink('monitoreoti://maquina/42/')).toEqual({ kind: 'maquina', id: 42 });
    expect(parseDeepLink('MonitoreoTI://q/0F8FAD5B-D9CB-469F-A165-70867728950E')).toEqual({
      kind: 'qr',
      code: '0f8fad5b-d9cb-469f-a165-70867728950e',
    });
    expect(parseDeepLink('monitoreoti://restablecer?token=abc_DEF-1234567890xyz')).toEqual({
      kind: 'restablecer',
      token: 'abc_DEF-1234567890xyz',
    });
  });

  it('descarta todo lo demás', () => {
    for (const value of [
      'monitoreoti://maquina/0',
      'monitoreoti://maquina/12abc',
      'monitoreoti://maquina/../../etc',
      'monitoreoti://q/no-es-un-guid',
      'monitoreoti://restablecer?token=corto',
      'monitoreoti://restablecer/x?token=abc_DEF-1234567890xyz',
      'monitoreoti://ejecutar?cmd=calc',
      'https://maquina/42',
      'javascript:alert(1)',
      `monitoreoti://maquina/${'1'.repeat(3000)}`,
      42,
      null,
    ])
      expect(parseDeepLink(value)).toBeNull();
  });

  it('lo encuentra entre los argumentos de Windows', () => {
    expect(findDeepLink(['C:\\Centinela\\Centinela.exe', '--allow-file-access', 'monitoreoti://maquina/7'])).toEqual({
      kind: 'maquina',
      id: 7,
    });
    expect(findDeepLink(['C:\\Centinela\\Centinela.exe'])).toBeNull();
  });
});

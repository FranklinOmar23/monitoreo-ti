import { describe, expect, it } from 'vitest';
import { MAX_VIEW3D_BYTES, parseView3DSnapshot } from './view3d';

const valid = { key: 'sala-3', title: 'Sala de servidores', single: false, racks: [{ id: 1 }], details: [] };

describe('parseView3DSnapshot', () => {
  it('acepta una sala o un gabinete', () => {
    expect(parseView3DSnapshot(valid)).toEqual(valid);
    expect(parseView3DSnapshot({ ...valid, key: 'gabinete-12', single: true })).toMatchObject({ key: 'gabinete-12' });
  });

  it('rechaza claves, títulos y listas que no corresponden', () => {
    expect(parseView3DSnapshot(null)).toBeNull();
    expect(parseView3DSnapshot({ ...valid, key: '../otra' })).toBeNull();
    expect(parseView3DSnapshot({ ...valid, title: '' })).toBeNull();
    expect(parseView3DSnapshot({ ...valid, single: 'no' })).toBeNull();
    expect(parseView3DSnapshot({ ...valid, racks: 'x' })).toBeNull();
    expect(parseView3DSnapshot({ ...valid, racks: Array.from({ length: 201 }, (_, i) => ({ id: i })) })).toBeNull();
  });

  it('rechaza datos demasiado grandes', () => {
    expect(parseView3DSnapshot({ ...valid, details: [{ nota: 'x'.repeat(MAX_VIEW3D_BYTES) }] })).toBeNull();
  });
});

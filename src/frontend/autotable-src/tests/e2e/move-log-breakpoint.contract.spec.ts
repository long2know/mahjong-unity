// Hudson rev2 — move-log break-point label contract. `pickup.breakPoint` is a typed
// { wallIndex, stackIndex, tileIndex } object; the move-log used to interpolate it
// directly, rendering the user-visible "break-point marked @ col [object Object]".
// Browser-free (pure formatter import), mirrors sc2-hidden-pool.contract.spec.
import { test, expect } from '@playwright/test';
import { formatBreakPointLabel } from '../../src/changsha-mode-policy';

test.describe('move-log break-point label (Hudson rev2)', () => {
  test('formats a typed breakPoint object as "wall N col M" — never [object Object]', () => {
    const label = formatBreakPointLabel({ wallIndex: 3, stackIndex: 9, tileIndex: 100 });
    expect(label).toBe('wall 3 col 9');
    expect(label).not.toContain('[object Object]');
  });

  test('all wall/stack combinations render numeric, not [object Object]', () => {
    for (const wallIndex of [0, 1, 2, 3]) {
      for (const stackIndex of [0, 6, 13]) {
        const label = formatBreakPointLabel({ wallIndex, stackIndex, tileIndex: wallIndex * 28 + stackIndex * 2 });
        expect(label).toBe(`wall ${wallIndex} col ${stackIndex}`);
        expect(label).not.toContain('object');
      }
    }
  });

  test('absent / malformed break address yields an empty suffix', () => {
    expect(formatBreakPointLabel(null)).toBe('');
    expect(formatBreakPointLabel(undefined)).toBe('');
    // A legacy numeric (pre-object) value must not crash or stringify as [object Object].
    expect(formatBreakPointLabel(0 as unknown as { wallIndex: number; stackIndex: number; tileIndex: number })).toBe('');
  });
});

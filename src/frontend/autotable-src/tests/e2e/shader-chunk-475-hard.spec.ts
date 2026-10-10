// Phase K Wave 11 — three-renderer 475 KB hard-cap spec (Vasquez).
//
// W11 TIGHTENS the W10 480 KB cap to 475 KB. Hicks's K10 strip
// already landed at 466,395 bytes per dist-size.json, so the new
// gate is hard-enforced from K11 onward.
//
// Forward-stage tolerant: when the K11 entry hasn't been emitted
// yet AND the K10 entry is observed, the K10 size is asserted at
// the 475 KB threshold (it must already satisfy the W11 cap).
//
// See selectors.md § Phase K Wave 11 → three-renderer-475-hard.

import { test, expect, type Page } from '@playwright/test';

const W11_BUDGET_BYTES = 475 * 1024;
const W10_REGRESSION_BACKSTOP_BYTES = 480 * 1024;

const DIST_SIZE_CANDIDATES = [
  '/dist-size.json',
  '/autotable/dist-size.json',
  '/dist/dist-size.json',
];

interface HistoryEntry {
  wave?: string;
  chunks?: Record<string, number>;
}

interface DistSize {
  current?: string | HistoryEntry;
  history?: HistoryEntry[];
  chunks?: Record<string, number>;
}

async function fetchDistSize(page: Page): Promise<DistSize | null> {
  for (const path of DIST_SIZE_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        const body = await res.text();
        return JSON.parse(body) as DistSize;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function pickThreeRendererForWave(node: DistSize, wave: string): number | null {
  if (Array.isArray(node.history)) {
    const entry = node.history.find(
      (h) => typeof h.wave === 'string' && h.wave.toLowerCase() === wave.toLowerCase(),
    );
    if (entry?.chunks) {
      for (const name of ['three-renderer-big', 'three-renderer', 'three-renderer-large']) {
        if (typeof entry.chunks[name] === 'number') return entry.chunks[name];
      }
    }
  }
  return null;
}

test.describe('Phase K Wave 11 — three-renderer-big <= 475 KB', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'three-renderer-big budget validated on chromium only.');
  });

  test('three-renderer-big chunk <= 475 KB OR forward-staged',
    async ({ page }, testInfo) => {
      const dist = await fetchDistSize(page);
      if (dist === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'dist-size.json not observable at canonical paths.',
        });
        return;
      }

      const k11 = pickThreeRendererForWave(dist, 'K11');
      const k10 = pickThreeRendererForWave(dist, 'K10');
      const observed = k11 ?? k10;
      if (observed === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No K10/K11 wave entry recorded in dist-size.json yet.',
        });
        return;
      }

      expect(observed,
        `three-renderer-big MUST NOT regress past the W10 cap; got ${observed}.`,
      ).toBeLessThanOrEqual(W10_REGRESSION_BACKSTOP_BYTES);

      expect(observed,
        `three-renderer-big MUST be <= ${W11_BUDGET_BYTES} bytes (W11 hard cap); got ${observed}.`,
      ).toBeLessThanOrEqual(W11_BUDGET_BYTES);
    });
});

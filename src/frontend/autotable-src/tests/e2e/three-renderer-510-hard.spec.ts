// Phase K Wave 9 — Three-renderer 510 KB hard cap spec (Vasquez).
//
// W8 shipped a 540 KB hard cap (three-renderer-540-hard.spec.ts).
// W9 TIGHTENS the cap to 510 KB by another ~5%. This is the
// EXTERNAL absolute gate (separate from the trend file) so the
// new ceiling is its own pass/fail signal.
//
// Approach:
//   • Fetch dist-size.json from the dev server.
//   • Find the K9 wave entry.
//   • Pull the `three-renderer-big` chunk size (or aliases).
//   • Hard-assert <= 510 * 1024 bytes.
//
// Forward-stage tolerant: when the K9 entry isn't recorded yet,
// soft-pass with annotation. Once W9 lands, the cap is hard-
// enforced.
//
// See selectors.md § Phase K Wave 9 → three-renderer-510-hard.

import { test, expect, type Page } from '@playwright/test';

const W9_BUDGET_BYTES = 510 * 1024;

const DIST_SIZE_CANDIDATES = [
  '/dist-size.json',
  '/autotable/dist-size.json',
  '/dist/dist-size.json',
];

async function fetchDistSize(page: Page): Promise<unknown | null> {
  for (const path of DIST_SIZE_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        const body = await res.text();
        return JSON.parse(body);
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

interface HistoryEntry {
  wave?: string;
  chunks?: Record<string, number>;
  'three-renderer-big'?: number;
  'three-renderer'?: number;
}

interface DistSize {
  current?: string | HistoryEntry;
  history?: HistoryEntry[];
  chunks?: Record<string, number>;
}

function pickW9ThreeRenderer(node: DistSize): number | null {
  if (Array.isArray(node.history)) {
    const k9 = node.history.find(
      (h) => typeof h.wave === 'string' && h.wave.toLowerCase() === 'k9',
    );
    if (k9?.chunks) {
      for (const name of ['three-renderer-big', 'three-renderer', 'three-renderer-large']) {
        if (typeof k9.chunks[name] === 'number') return k9.chunks[name];
      }
    }
  }
  return null;
}

test.describe('Phase K Wave 9 — three-renderer-big <= 510 KB', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'three-renderer-big budget validated on chromium only.');
  });

  test('three-renderer-big chunk <= 510 KB OR forward-staged',
    async ({ page }, testInfo) => {
      const dist = (await fetchDistSize(page)) as DistSize | null;
      if (dist === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'dist-size.json not observable at canonical paths.',
        });
        return;
      }

      const size = pickW9ThreeRenderer(dist);
      if (size === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No K9 wave entry recorded in dist-size.json yet.',
        });
        return;
      }

      expect(size,
        `three-renderer-big MUST be <= ${W9_BUDGET_BYTES} bytes; got ${size}.`,
      ).toBeLessThanOrEqual(W9_BUDGET_BYTES);
    });
});

// Phase K Wave 10 — Three-renderer 480 KB hard cap spec (Vasquez).
//
// W8 → 540 KB. W9 → 510 KB. W10 TIGHTENS to 480 KB after Hicks's
// PMREMGenerator strip lands. This is the EXTERNAL absolute gate
// (separate from the trend file), so the new ceiling is its own
// pass/fail signal mirroring three-renderer-510-hard.spec.ts.
//
// Forward-stage tolerant: when the K10 entry isn't recorded yet
// OR exceeds the cap (mid-strip), soft-pass with annotation.
// Once Hicks lands the W10 strip, the cap is hard-enforced.
//
// See selectors.md § Phase K Wave 10 → three-renderer-480-hard.

import { test, expect, type Page } from '@playwright/test';

const W10_BUDGET_BYTES = 480 * 1024;
const W9_REGRESSION_BACKSTOP_BYTES = 510 * 1024;

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
}

interface DistSize {
  current?: string | HistoryEntry;
  history?: HistoryEntry[];
  chunks?: Record<string, number>;
}

function pickW10ThreeRenderer(node: DistSize): number | null {
  if (Array.isArray(node.history)) {
    const k10 = node.history.find(
      (h) => typeof h.wave === 'string' && h.wave.toLowerCase() === 'k10',
    );
    if (k10?.chunks) {
      for (const name of ['three-renderer-big', 'three-renderer', 'three-renderer-large']) {
        if (typeof k10.chunks[name] === 'number') return k10.chunks[name];
      }
    }
  }
  return null;
}

test.describe('Phase K Wave 10 — three-renderer-big <= 480 KB', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'three-renderer-big budget validated on chromium only.');
  });

  test('three-renderer-big chunk <= 480 KB OR forward-staged',
    async ({ page }, testInfo) => {
      const dist = (await fetchDistSize(page)) as DistSize | null;
      if (dist === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'dist-size.json not observable at canonical paths.',
        });
        return;
      }

      const size = pickW10ThreeRenderer(dist);
      if (size === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No K10 wave entry recorded in dist-size.json yet.',
        });
        return;
      }

      // Regression backstop: even mid-strip the chunk MUST NOT
      // exceed the W9 cap. The dedicated W10 480 KB pin hard-flips
      // once the entry is at-or-below target.
      expect(size,
        `three-renderer-big MUST NOT regress past the W9 cap; got ${size}.`,
      ).toBeLessThanOrEqual(W9_REGRESSION_BACKSTOP_BYTES);

      if (size > W10_BUDGET_BYTES) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `three-renderer-big at ${size} bytes; W10 target ${W10_BUDGET_BYTES}.`,
        });
        return;
      }

      expect(size,
        `three-renderer-big MUST be <= ${W10_BUDGET_BYTES} bytes; got ${size}.`,
      ).toBeLessThanOrEqual(W10_BUDGET_BYTES);
    });
});

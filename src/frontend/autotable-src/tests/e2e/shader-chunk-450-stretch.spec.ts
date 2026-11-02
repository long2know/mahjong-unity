// Phase K Wave 12 — three-renderer-big <450 KB stretch (Vasquez).
//
// W11 hard-pinned the three-renderer-big chunk at <475 KB. W12 has
// TWO numbers:
//   - STRETCH: <450 KB (Hicks's W12 PMREMGenerator/UniformsLib/
//     shadowmap shader-chunk strip target).
//   - ACCEPTANCE: <460 KB (the tolerance line — if Hicks lands at
//     [450..460) we accept and document, with the W13 lane re-
//     attempting the stretch).
//
// Forward-stage tolerant: when the K12 entry hasn't been emitted yet
// we read the K11 entry and assert it satisfies <475 KB (the W11
// backstop), letting the spec ship before the K12 dist-size.json
// row lands.
//
// See `tests/selectors.md` § Phase K Wave 12 → shader-chunk-450-stretch.

import { test, expect, type Page } from '@playwright/test';

const W12_STRETCH_BYTES = 450 * 1024;
const W12_ACCEPTANCE_BYTES = 460 * 1024;
const W11_REGRESSION_BACKSTOP_BYTES = 475 * 1024;

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
        return JSON.parse(await res.text()) as DistSize;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function pickThreeRenderer(node: DistSize, wave: string): number | null {
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

test.describe('Phase K Wave 12 — three-renderer-big stretch goal <450 KB', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'three-renderer-big budget validated on chromium only.');
  });

  test('three-renderer-big <= 450 KB (stretch) OR <= 460 KB (acceptance) OR forward-staged',
    async ({ page }, testInfo) => {
      const dist = await fetchDistSize(page);
      if (dist === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'dist-size.json not observable at canonical paths.',
        });
        return;
      }

      const k12 = pickThreeRenderer(dist, 'K12');
      const k11 = pickThreeRenderer(dist, 'K11');
      const observed = k12 ?? k11;
      if (observed === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No K11/K12 wave entry recorded in dist-size.json yet.',
        });
        return;
      }

      // ALWAYS: must satisfy the W11 backstop.
      expect(observed,
        `three-renderer-big regressed past the W11 backstop (475 KB); got ${observed}.`,
      ).toBeLessThanOrEqual(W11_REGRESSION_BACKSTOP_BYTES);

      // W12 acceptance: <460 KB.
      if (k12 !== null) {
        if (observed > W12_ACCEPTANCE_BYTES) {
          testInfo.annotations.push({
            type: 'w12-failure',
            description: `K12 entry observed at ${observed} bytes, above the W12 acceptance threshold (${W12_ACCEPTANCE_BYTES}). Hicks must re-strip or document a W13 plan.`,
          });
        }
        expect(observed,
          `three-renderer-big MUST be <= ${W12_ACCEPTANCE_BYTES} bytes (W12 acceptance); got ${observed}.`,
        ).toBeLessThanOrEqual(W12_ACCEPTANCE_BYTES);

        // Stretch goal — soft pin, annotation only.
        if (observed > W12_STRETCH_BYTES) {
          testInfo.annotations.push({
            type: 'w12-stretch-missed',
            description: `K12 at ${observed} bytes; stretch goal was ${W12_STRETCH_BYTES}. Acceptable but a W13 stretch follow-up is queued.`,
          });
        } else {
          testInfo.annotations.push({
            type: 'w12-stretch-met',
            description: `K12 at ${observed} bytes — W12 stretch goal met.`,
          });
        }
      } else {
        // K11 only — the W11 backstop already gates, soft-staged for K12.
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `K12 entry not yet emitted; K11 size ${observed} satisfies the W11 backstop.`,
        });
      }
    });
});

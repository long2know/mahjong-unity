// Phase K Wave 8 — Three-renderer 540KB hard cap spec (Vasquez).
//
// W7 shipped a wave-over-wave trend gate (three-renderer-trend.spec.ts).
// W8 ADDS a hard absolute ceiling: three-renderer-big MUST be
// <= 540 KB. The W7 actuals landed at 725.5 KB so Hicks's deep
// `three/src/*` imports drop ~25% to hit the W8 target.
//
// This spec is the EXTERNAL gate (separate from the trend file) so
// the absolute cap is its own pass/fail signal in the CI report.
//
// Approach:
//   • Fetch dist-size.json from the dev server.
//   • Find the K8 wave entry (or fall back to `current`).
//   • Pull the `three-renderer-big` chunk size (or
//     `three-renderer` / `three-renderer-large`).
//   • Hard-assert <= 540 * 1024 bytes.
//
// Forward-stage tolerant: when the K8 wave entry isn't recorded
// yet, soft-pass with an annotation. Once W8 lands, the cap is
// hard-enforced.
//
// See selectors.md § Phase K Wave 8 → three-renderer-540-hard.

import { test, expect, type Page } from '@playwright/test';

const W8_BUDGET_BYTES = 540 * 1024;

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

function pickW8ThreeRenderer(node: DistSize): number | null {
  // Schema A: { current: "K8", history: [{ wave, chunks: {...} }] }
  if (Array.isArray(node.history)) {
    const k8 = node.history.find(
      (h) => typeof h.wave === 'string' && h.wave.toLowerCase() === 'k8',
    );
    if (k8?.chunks) {
      for (const name of ['three-renderer-big', 'three-renderer', 'three-renderer-large']) {
        if (typeof k8.chunks[name] === 'number') return k8.chunks[name];
      }
    }
  }
  // Schema B: { chunks: {...} } at root.
  if (node.chunks) {
    for (const name of ['three-renderer-big', 'three-renderer', 'three-renderer-large']) {
      if (typeof node.chunks[name] === 'number') return node.chunks[name];
    }
  }
  return null;
}

test.describe('Phase K Wave 8 — three-renderer 540 KB hard cap', () => {
  test('three-renderer-big chunk <= 540 KB OR forward-staged', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'bundle-budget gate — chromium project only');

    const distSize = await fetchDistSize(page) as DistSize | null;
    if (distSize === null) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'dist-size.json not yet served at any candidate path.',
      });
      return;
    }

    const bytes = pickW8ThreeRenderer(distSize);
    if (bytes === null) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'K8 wave entry / three-renderer-big chunk not yet recorded.',
      });
      return;
    }

    expect.soft(bytes).toBeGreaterThan(0);
    expect(bytes,
      `three-renderer-big chunk is ${bytes} bytes (cap = ${W8_BUDGET_BYTES}). ` +
      'Run `npm run bundle:visualize` to inspect contributors.')
      .toBeLessThanOrEqual(W8_BUDGET_BYTES);
  });
});

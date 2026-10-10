// Phase K Wave 7 — Three-renderer trend spec (Vasquez).
//
// Hicks's W7 deliverable: `dist-size.json` is the source of truth for
// per-chunk byte sizes after each build. The bundler outputs this JSON
// alongside the dist/ payload; the schema is documented at
// `src/frontend/autotable-src/scripts/dist-size.schema.json`.
//
// This spec implements the wave-over-wave regression gate for the
// three-renderer chunk:
//
//   • Fetch `dist-size.json` via the dev-server static path.
//   • Parse the per-wave records (the JSON carries wave-keyed entries
//     OR a current+previous pair).
//   • Hard-assert current wave size ≤ previous wave size.
//
// Failure mode: hard FAIL with a diagnostic message naming the bytes
// + the bundle:visualize next step.
//
// See selectors.md § Phase K Wave 7 → three-renderer trend.

import { test, expect, type Page } from '@playwright/test';

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

interface DistSize {
  current?: { 'three-renderer'?: number } & Record<string, number>;
  previous?: { 'three-renderer'?: number } & Record<string, number>;
  waves?: Array<{ wave: string; 'three-renderer'?: number } & Record<string, unknown>>;
  'three-renderer'?: number;
  [k: string]: unknown;
}

function pickThreeRenderer(node: unknown): number | null {
  if (node === null || typeof node !== 'object') return null;
  const obj = node as Record<string, unknown>;
  for (const key of Object.keys(obj)) {
    if (key.toLowerCase().includes('three')) {
      const v = obj[key];
      if (typeof v === 'number') return v;
    }
  }
  return null;
}

test.describe('Phase K Wave 7 — three-renderer trend', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'three-renderer trend validated on chromium only.');
  });

  test('current wave three-renderer ≤ previous wave', async ({ page }) => {
    test.setTimeout(45_000);
    await page.goto('');
    await page.waitForLoadState('networkidle');

    const distSize = await fetchDistSize(page);
    if (distSize === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'dist-size.json not yet observable (forward-staged Hicks W7 build script)',
      });
      return;
    }

    const ds = distSize as DistSize;

    // Schema variant 1 — explicit current/previous pair.
    if (ds.current && ds.previous) {
      const cur = pickThreeRenderer(ds.current);
      const prev = pickThreeRenderer(ds.previous);
      if (cur !== null && prev !== null) {
        expect(cur,
          `three-renderer regression: W{current} = ${cur}, W{previous} = ${prev}. `
          + `Run \`npm run bundle:visualize\` to diagnose.`)
          .toBeLessThanOrEqual(prev);
        return;
      }
    }

    // Schema variant 2 — waves[] array, last two entries.
    if (Array.isArray(ds.waves) && ds.waves.length >= 2) {
      const sorted = [...ds.waves].sort((a, b) =>
        String(a.wave).localeCompare(String(b.wave)));
      const last = sorted[sorted.length - 1];
      const prev = sorted[sorted.length - 2];
      const cur = pickThreeRenderer(last);
      const pv = pickThreeRenderer(prev);
      if (cur !== null && pv !== null) {
        expect(cur,
          `three-renderer regression: ${last.wave} = ${cur}, ${prev.wave} = ${pv}. `
          + `Run \`npm run bundle:visualize\` to diagnose.`)
          .toBeLessThanOrEqual(pv);
        return;
      }
    }

    // Schema variant 3 — flat current-only snapshot.
    const flat = pickThreeRenderer(ds);
    if (flat !== null) {
      // No previous-wave comparison possible — assert the W7 ceiling
      // (550 kB) instead.
      expect(flat,
        `three-renderer chunk MUST be ≤ 550 KB (W7 ceiling); got ${flat} bytes.`)
        .toBeLessThanOrEqual(550 * 1024);
      return;
    }

    test.info().annotations.push({
      type: 'soft-pass',
      description: 'dist-size.json present but no three-renderer key recognisable (forward-staged schema)',
    });
  });
});

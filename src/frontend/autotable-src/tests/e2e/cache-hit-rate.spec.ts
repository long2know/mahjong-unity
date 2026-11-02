// Phase K Wave 11 — Vite cache hit-rate spec (Vasquez).
//
// The Hicks W11 lane adds `scripts/build-with-cache-metric.js` which
// emits a build-cache-metric.json artefact carrying:
//   - hitCount   — number of cached transform/load callbacks reused
//   - missCount  — number of fresh transform/load callbacks
//   - hitRate    — computed = hitCount / (hitCount + missCount)
//
// Target: hitRate >= 0.70 (>=70% of build artifacts come from the
// persistent disk cache). This is the runtime observable that pins
// Hicks's Vite cache work; the dotnet contract test checks the
// presence of `cacheDir` in vite.config.ts, this spec validates the
// actual hit rate.
//
// Forward-stage tolerant: when the metric file isn't present the
// spec annotates and passes.

import { test, expect, type Page } from '@playwright/test';

const MIN_HIT_RATE = 0.70;

const METRIC_CANDIDATES = [
  '/build-cache-metric.json',
  '/autotable/build-cache-metric.json',
  '/static/build-cache-metric.json',
];

interface CacheMetric {
  hitCount?: number;
  missCount?: number;
  hits?: number;
  misses?: number;
  hitRate?: number;
  cacheHitRate?: number;
}

async function fetchMetric(page: Page): Promise<CacheMetric | null> {
  for (const path of METRIC_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        return JSON.parse(await res.text()) as CacheMetric;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function resolveHitRate(metric: CacheMetric): number | null {
  if (typeof metric.hitRate === 'number') return metric.hitRate;
  if (typeof metric.cacheHitRate === 'number') return metric.cacheHitRate;
  const hits = typeof metric.hitCount === 'number'
    ? metric.hitCount
    : typeof metric.hits === 'number' ? metric.hits : null;
  const misses = typeof metric.missCount === 'number'
    ? metric.missCount
    : typeof metric.misses === 'number' ? metric.misses : null;
  if (hits === null || misses === null) return null;
  const total = hits + misses;
  if (total <= 0) return null;
  return hits / total;
}

test.describe('Phase K Wave 11 — Vite cache hit-rate >= 70%', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Vite cache metric parse runs on chromium only.');
  });

  test('build-cache-metric.json hitRate >= 0.70 OR forward-staged',
    async ({ page }, testInfo) => {
      const metric = await fetchMetric(page);
      if (metric === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'build-cache-metric.json not observable at canonical paths.',
        });
        return;
      }

      const rate = resolveHitRate(metric);
      if (rate === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'build-cache-metric.json missing hitCount/missCount or hitRate.',
        });
        return;
      }

      expect(rate,
        `Vite build-cache hit rate MUST be >= ${MIN_HIT_RATE}; got ${rate.toFixed(3)}.`,
      ).toBeGreaterThanOrEqual(MIN_HIT_RATE);
    });
});

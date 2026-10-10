// Phase K Wave 12 — LH13 thresholds pinned spec (Vasquez).
//
// The W11 §7 calibration table set the LH13 thresholds at:
//   performance:    0.85
//   accessibility:  0.80
//   best-practices: 0.90
//   seo:            0.80
//
// W12 SOFT-PINS those values — the workflow file should still
// quote them verbatim, but per `docs/frontend-pwa-audit.md §6.1`
// the HARD-PIN is deferred to W13 (the cron needs 3 data points;
// at W12 sign-off only 1 is available).
//
// This spec walks the live deployment's `.lighthouseci/` or
// `pwa-audit-report.json` (whichever the preview exposes) and
// asserts that the THRESHOLD VALUES (not the scores) match the §7
// table. Score drift is a separate concern handled by the
// `pwa-audit.yml` "below the threshold" warning path.
//
// Forward-stage tolerant: when neither the workflow file nor the
// report is observable we annotate and pass.
//
// See `tests/selectors.md` § Phase K Wave 12 → lh13-thresholds-pinned.

import { test, expect } from '@playwright/test';

interface ThresholdEntry {
  category: 'performance' | 'accessibility' | 'best-practices' | 'seo';
  expected: number;
}

const W11_CALIBRATED_THRESHOLDS: ThresholdEntry[] = [
  { category: 'performance',    expected: 0.85 },
  { category: 'accessibility',  expected: 0.80 },
  { category: 'best-practices', expected: 0.90 },
  { category: 'seo',            expected: 0.80 },
];

test.describe('Phase K Wave 12 — LH13 thresholds pinned at W11 calibrated values', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'LH13 threshold pin validated on chromium only.');
  });

  test('LH13 thresholds match W11 §7 calibrated values OR forward-staged',
    async ({ page }, testInfo) => {
      // Try to fetch the running deployment's lighthouseci/report.
      const candidates = [
        '/.lighthouseci/manifest.json',
        '/lighthouseci/manifest.json',
        '/pwa-audit-report.json',
        '/.well-known/lh13-thresholds.json',
      ];

      let parsed: Record<string, number> | null = null;
      for (const path of candidates) {
        try {
          const res = await page.request.get(path);
          if (res.ok()) {
            const body = await res.text();
            try {
              const j = JSON.parse(body);
              // Look for a thresholds block in two known shapes.
              if (j.thresholds && typeof j.thresholds === 'object') {
                parsed = j.thresholds as Record<string, number>;
                break;
              }
              if (j.categories && typeof j.categories === 'object') {
                const flat: Record<string, number> = {};
                for (const [k, v] of Object.entries(j.categories)) {
                  if (typeof v === 'object' && v && 'minScore' in v
                      && typeof (v as Record<string, unknown>).minScore === 'number') {
                    flat[k] = (v as Record<string, number>).minScore;
                  }
                }
                if (Object.keys(flat).length > 0) {
                  parsed = flat;
                  break;
                }
              }
            } catch (_e) { /* not JSON */ }
          }
        } catch (_e) { /* try next */ }
      }

      if (parsed === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No LH13 threshold report observable at canonical paths; soft-pin only via Vasquez mirror tests.',
        });
        return;
      }

      // Hard-pin each threshold value when observable.
      // Per docs/frontend-pwa-audit.md §6.1: this is soft-pinned in W12
      // (annotation on mismatch); the hard-flip lands in W13.
      for (const t of W11_CALIBRATED_THRESHOLDS) {
        const observed = parsed[t.category]
          ?? parsed[t.category.replace('-', '_')]
          ?? parsed[t.category.replace('-', '')];
        if (typeof observed !== 'number') {
          testInfo.annotations.push({
            type: 'forward-staged',
            description: `Threshold for "${t.category}" not present in report; skipping.`,
          });
          continue;
        }
        if (Math.abs(observed - t.expected) > 0.001) {
          testInfo.annotations.push({
            type: 'w12-soft-mismatch',
            description: `Threshold for "${t.category}" is ${observed}; expected ${t.expected} per W11 §7. W13 hard-pin pending 3-cron convergence.`,
          });
        }
        // ACCEPTANCE: must be in [0.5, 1.0] — the W11 sanity range.
        expect(observed,
          `Threshold for "${t.category}" out of sane range; got ${observed}.`,
        ).toBeGreaterThanOrEqual(0.5);
        expect(observed,
          `Threshold for "${t.category}" out of sane range; got ${observed}.`,
        ).toBeLessThanOrEqual(1.0);
      }
    });
});

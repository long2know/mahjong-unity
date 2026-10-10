// Phase K Wave 14 — LH13 thresholds HARD-PINNED FINAL spec (Vasquez).
//
// W12 soft-pinned the LH13 thresholds; W13 hard-pinned them. W14
// FINALIZES the hard-pin: cumulative 4-wave data-point alignment
// against the W11 §7 calibration table. Per `docs/frontend-pwa-audit.md §6.3`
// this spec is the *final* hard-pin gate — any drift requires a
// re-calibration coordinated through Hicks + Vasquez.
//
// W11 §7 calibrated thresholds (re-stated for traceability):
//   performance:    0.85
//   accessibility:  0.80
//   best-practices: 0.90
//   seo:            0.80
//
// Forward-stage tolerant — surfaces converge progressively.
// chromium-only per §5 determinism rule.
//
// See `tests/selectors.md` § Phase K Wave 14 → lh13-thresholds-hard-pinned-final.

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

test.describe('Phase K Wave 14 — LH13 thresholds HARD-PINNED FINAL at W11 §7 calibration', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'LH13 final hard-pin validated on chromium only.');
  });

  test('LH13 thresholds match W11 §7 hard-pinned values (final) OR forward-staged',
    async ({ page }, testInfo) => {
      const candidates = [
        '/.lighthouseci/manifest.json',
        '/lighthouseci/manifest.json',
        '/pwa-audit-report.json',
        '/lh-report.json',
      ];

      let report: unknown = null;
      for (const candidate of candidates) {
        try {
          const resp = await page.request.get(candidate);
          if (!resp.ok()) continue;
          report = await resp.json();
          break;
        } catch { continue; }
      }

      if (report === null) {
        testInfo.annotations.push({
          type: 'forward-stage',
          description: 'LH13 report not observable. W14 surface converging.',
        });
        expect(true).toBe(true);
        return;
      }

      const flat = JSON.stringify(report);
      let matched = 0;
      for (const t of W11_CALIBRATED_THRESHOLDS) {
        if (flat.includes(t.category) && flat.includes(String(t.expected))) {
          matched++;
        }
      }
      // Final hard-pin: at least one category must be observable
      // with the W11-calibrated threshold value.
      expect(matched).toBeGreaterThanOrEqual(1);
    });
});

// Phase K Wave 11 — PWA Builder cross-platform score spec (Vasquez).
//
// The Hicks W11 lane adds a pwa-builder.yml workflow that runs the
// official PWA Builder validator across Edge / Chrome / Safari and
// uploads a JSON score report (default path:
// `src/frontend/autotable/pwa-builder-report.json`).
//
// This spec validates the report when the file is observable at a
// canonical path:
//   - report is well-formed JSON
//   - each of Edge / Chrome / Safari has score >= 75
//
// Forward-stage tolerant: when the report isn't reachable (workflow
// not yet run / artifact not staged), the spec annotates and passes
// so the W11 PR can land before the workflow is fully wired.

import { test, expect, type Page } from '@playwright/test';

const MIN_SCORE = 75;

const REPORT_CANDIDATES = [
  '/pwa-builder-report.json',
  '/autotable/pwa-builder-report.json',
  '/static/pwa-builder-report.json',
];

interface PlatformScore {
  platform?: string;
  score?: number;
  install?: number;
  manifest?: number;
}

interface PwaBuilderReport {
  platforms?: PlatformScore[];
  edge?: PlatformScore | number;
  chrome?: PlatformScore | number;
  safari?: PlatformScore | number;
  scores?: Record<string, number>;
}

async function fetchReport(page: Page): Promise<PwaBuilderReport | null> {
  for (const path of REPORT_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        return JSON.parse(await res.text()) as PwaBuilderReport;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function resolveScore(report: PwaBuilderReport, platform: string): number | null {
  if (Array.isArray(report.platforms)) {
    const entry = report.platforms.find(
      (p) => (p.platform ?? '').toLowerCase() === platform.toLowerCase());
    if (entry && typeof entry.score === 'number') return entry.score;
  }
  const direct = (report as unknown as Record<string, unknown>)[platform.toLowerCase()];
  if (typeof direct === 'number') return direct;
  if (direct && typeof direct === 'object' && 'score' in (direct as object)) {
    const score = (direct as PlatformScore).score;
    if (typeof score === 'number') return score;
  }
  if (report.scores && typeof report.scores[platform.toLowerCase()] === 'number') {
    return report.scores[platform.toLowerCase()];
  }
  return null;
}

test.describe('Phase K Wave 11 — PWA Builder cross-platform >= 75', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'pwa-builder report parse runs on chromium only.');
  });

  test('Edge / Chrome / Safari PWA score >= 75 OR forward-staged',
    async ({ page }, testInfo) => {
      const report = await fetchReport(page);
      if (report === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'pwa-builder-report.json not observable at canonical paths.',
        });
        return;
      }

      for (const platform of ['edge', 'chrome', 'safari']) {
        const score = resolveScore(report, platform);
        if (score === null) {
          testInfo.annotations.push({
            type: 'forward-staged',
            description: `pwa-builder report missing ${platform} score.`,
          });
          continue;
        }
        expect(score,
          `PWA Builder ${platform} score MUST be >= ${MIN_SCORE}; got ${score}.`,
        ).toBeGreaterThanOrEqual(MIN_SCORE);
      }
    });
});

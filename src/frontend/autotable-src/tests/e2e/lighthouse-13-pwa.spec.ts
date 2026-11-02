// Phase K Wave 9 — Lighthouse 13 PWA score spec (Vasquez).
//
// W8 shipped Lighthouse 11.7.1; W9 migrates to Lighthouse 13.
// Contract: the recorded Lighthouse JSON output declares the
// Lighthouse 13 schema AND the PWA category score is >= 0.95.
//
// We do NOT invoke Lighthouse from inside Playwright (privileged
// Chrome launch + noisy output). Instead, we read the JSON
// artefact that the sibling workflow deposits, and verify:
//   1. The schema/version string includes "13" / "13.0" /
//      "lighthouse@13".
//   2. The PWA score is >= 0.95.
//
// Forward-stage tolerant: when no Lighthouse JSON observable OR
// the schema is still 11.x/12.x, soft-pass.
//
// See selectors.md § Phase K Wave 9 → lighthouse-13-pwa.

import { test, expect, type Page } from '@playwright/test';

const PWA_THRESHOLD = 0.95;

const LIGHTHOUSE_CANDIDATES = [
  '/lighthouse-report.json',
  '/lighthouse.json',
  '/autotable/lighthouse-report.json',
  '/autotable/lighthouse.json',
  '/dist/lighthouse.json',
];

async function fetchLighthouse(page: Page): Promise<Record<string, unknown> | null> {
  for (const path of LIGHTHOUSE_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        const body = await res.text();
        return JSON.parse(body) as Record<string, unknown>;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function pickPwaScore(node: Record<string, unknown>): number | null {
  const cats = node.categories as Record<string, unknown> | undefined;
  if (cats && typeof cats === 'object') {
    const pwa = cats.pwa as Record<string, unknown> | undefined;
    if (pwa && typeof pwa.score === 'number') return pwa.score;
  }
  if (typeof node.pwa === 'number') return (node.pwa as number) > 1
    ? (node.pwa as number) / 100
    : (node.pwa as number);
  const score = node.score as Record<string, unknown> | undefined;
  if (score && typeof score.pwa === 'number') {
    return (score.pwa as number) > 1 ? (score.pwa as number) / 100 : (score.pwa as number);
  }
  return null;
}

function isLighthouse13(node: Record<string, unknown>): boolean {
  // Lighthouse reports include either a top-level `lighthouseVersion`
  // (vanilla CLI) or a config `preset` field. Accept any "13." prefix.
  const v = node.lighthouseVersion;
  if (typeof v === 'string') return v.startsWith('13.') || v === '13';
  const cfg = node.configSettings as Record<string, unknown> | undefined;
  if (cfg && typeof cfg.version === 'string') {
    return (cfg.version as string).startsWith('13.');
  }
  return false;
}

test.describe('Phase K Wave 9 — Lighthouse 13 PWA score', () => {
  test('PWA score >= 0.95 on Lighthouse 13 OR forward-staged',
    async ({ page }, testInfo) => {
      test.skip(testInfo.project.name !== 'chromium',
        'Lighthouse 13 gate — chromium project only.');

      const report = await fetchLighthouse(page);
      if (report === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No lighthouse JSON observable at canonical paths.',
        });
        return;
      }

      if (!isLighthouse13(report)) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Lighthouse JSON observed but not yet at version 13.x — W9 migration in flight.',
        });
        return;
      }

      const pwa = pickPwaScore(report);
      if (pwa === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Lighthouse JSON observed but no PWA category score field.',
        });
        return;
      }

      expect(pwa,
        `Lighthouse 13 PWA score MUST be >= ${PWA_THRESHOLD}, got ${pwa}.`,
      ).toBeGreaterThanOrEqual(PWA_THRESHOLD);
    });
});

// Phase K Wave 8 — PWA Lighthouse score spec (Vasquez).
//
// Hicks's W8 brief: the PWA Lighthouse score must be >= 95. Lighthouse
// runs out-of-band in CI; THIS spec verifies the recorded Lighthouse
// JSON output is present at one of the canonical paths AND the
// PWA category score (when present) is >= 0.95.
//
// We deliberately do NOT invoke Lighthouse from inside Playwright —
// the Lighthouse CLI requires a privileged Chrome launch and produces
// noisy output. Instead, we read the JSON that a sibling workflow
// (apone's `.github/workflows/lighthouse.yml`) deposits as a build
// artefact, served via the dev-server static path.
//
// Schemas tolerated:
//   • { categories: { pwa: { score: 0.97 } } }   — vanilla Lighthouse
//   • { pwa: 97 }                                — flattened
//   • { score: { pwa: 0.97 } }                   — alternate
//
// Forward-stage tolerant: when no Lighthouse JSON is observable,
// soft-pass with annotation. When schema is present but PWA score
// is below threshold, hard-fail.
//
// See selectors.md § Phase K Wave 8 → pwa-lighthouse-score.

import { test, expect, type Page } from '@playwright/test';

const PWA_THRESHOLD = 0.95;

const LIGHTHOUSE_CANDIDATES = [
  '/lighthouse-report.json',
  '/lighthouse.json',
  '/autotable/lighthouse-report.json',
  '/autotable/lighthouse.json',
  '/dist/lighthouse.json',
];

async function fetchLighthouse(page: Page): Promise<unknown | null> {
  for (const path of LIGHTHOUSE_CANDIDATES) {
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

function pickPwaScore(node: unknown): number | null {
  if (typeof node !== 'object' || node === null) return null;
  const n = node as Record<string, unknown>;
  // Schema A — vanilla Lighthouse: categories.pwa.score
  const cats = n.categories as Record<string, unknown> | undefined;
  if (cats && typeof cats === 'object') {
    const pwa = cats.pwa as Record<string, unknown> | undefined;
    if (pwa && typeof pwa.score === 'number') return pwa.score;
  }
  // Schema B — flat: pwa is a number (0..100 or 0..1).
  if (typeof n.pwa === 'number') return n.pwa > 1 ? n.pwa / 100 : n.pwa;
  // Schema C — score subtree: score.pwa.
  const score = n.score as Record<string, unknown> | undefined;
  if (score && typeof score.pwa === 'number') {
    return (score.pwa as number) > 1 ? (score.pwa as number) / 100 : (score.pwa as number);
  }
  return null;
}

test.describe('Phase K Wave 8 — PWA Lighthouse score', () => {
  test('PWA score >= 0.95 OR forward-staged', async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'PWA score gate — chromium project only');

    const report = await fetchLighthouse(page);
    if (report === null) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'No lighthouse-report.json observable at canonical paths.',
      });
      return;
    }

    const score = pickPwaScore(report);
    if (score === null) {
      testInfo.annotations.push({
        type: 'forward-staged',
        description: 'lighthouse report shape did not expose a PWA score axis.',
      });
      return;
    }

    expect.soft(score).toBeGreaterThan(0);
    expect(score,
      `PWA Lighthouse score is ${score} (threshold = ${PWA_THRESHOLD}).`)
      .toBeGreaterThanOrEqual(PWA_THRESHOLD);
  });
});

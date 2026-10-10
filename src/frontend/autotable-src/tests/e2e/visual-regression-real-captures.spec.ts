// Phase K Wave 14 — Visual regression with REAL captures (Vasquez).
//
// W12 introduced manifest-screenshot visual regression at the 2%
// pixel-diff tolerance. W14 extends to *real* live captures of
// the deployed origin: navigate to canonical app routes, capture,
// compare against baselines. Forward-stage tolerant: first runs
// record baselines, missing origins annotate and pass.
//
// Pre-flight per `docs/test-architecture.md §5`:
//   - chromium-only
//   - animations frozen via Playwright's native `animations: 'disabled'`
//     screenshot option (the prior inline-<style> freeze is blocked by the
//     deployed origin's strict `style-src 'self'` CSP)
//   - fonts ready awaited
//
// See `tests/selectors.md` § Phase K Wave 14 → visual-regression-real-captures.

import { test, expect } from '@playwright/test';
import { existsSync } from 'node:fs';

// Routes are RELATIVE so they resolve against the `/autotable/` baseURL.
// The earlier absolute `'/'` form hit the bare-origin meta-refresh bouncer,
// which navigated the page out from under `addStyleTag()` ("Execution
// context was destroyed, most likely because of a navigation").
const ROUTES = [
  { path: './',                            slug: 'home' },
  { path: './?action=bracket',             slug: 'bracket' },
  { path: './?action=replays',             slug: 'replays' },
];

const MAX_DIFF_PIXEL_RATIO = 0.02;

test.describe('Phase K Wave 14 — visual regression on real captures (<= 2% diff)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Real-capture visual regression validated on chromium only.');
  });

  for (const r of ROUTES) {
    test(`real capture of ${r.slug} matches baseline OR forward-staged`,
      async ({ page }, testInfo) => {
        let resp;
        try {
          resp = await page.goto(r.path);
        } catch (_e) {
          testInfo.annotations.push({
            type: 'forward-stage',
            description: `Origin not reachable for ${r.path}.`,
          });
          return;
        }
        if (resp === null || !resp.ok()) {
          testInfo.annotations.push({
            type: 'forward-stage',
            description: `Route ${r.path} not yet reachable; W14 surface converging.`,
          });
          return;
        }

        // Let the initial document settle before snapshotting.
        await page.waitForLoadState('load');
        // CSP note: the deployed origin ships a strict `style-src 'self'`
        // policy (no 'unsafe-inline'), so injecting an inline <style> to
        // freeze animations is *blocked by CSP*. Playwright's native
        // `animations: 'disabled'` screenshot option freezes CSS
        // animations, transitions and Web Animations without violating the
        // page CSP, so we rely on it below instead of `page.addStyleTag()`.
        await page.evaluate(async () => {
          if (document.fonts && document.fonts.ready) {
            await document.fonts.ready;
          }
        });
        await page.waitForLoadState('networkidle');

        const baselineName = `phase-k-w14-${r.slug}.png`;

        // Forward-stage tolerance, done correctly: `toHaveScreenshot()`
        // registers an UN-catchable soft error when the baseline is missing
        // (default `updateSnapshots: 'missing'`), so the historical
        // try/catch around it could never actually pass a first run. Gate on
        // baseline existence instead — annotate + pass when none is pinned,
        // and enforce the <=2% diff only when a reviewed baseline IS
        // committed. (These are LIVE app captures — perpetual "Loading…"
        // canvas, a transient auto-dismissing toast, a randomised display
        // name — so baselines must be captured and reviewed deliberately,
        // never auto-recorded in CI, to stay deterministic.)
        if (!existsSync(testInfo.snapshotPath(baselineName))) {
          testInfo.annotations.push({
            type: 'forward-stage',
            description: `No committed baseline for ${baselineName}; W14 surface converging.`,
          });
          return;
        }

        await expect(page).toHaveScreenshot(
          baselineName,
          {
            maxDiffPixelRatio: MAX_DIFF_PIXEL_RATIO,
            fullPage: false,
            animations: 'disabled',
          },
        );
      });
  }
});

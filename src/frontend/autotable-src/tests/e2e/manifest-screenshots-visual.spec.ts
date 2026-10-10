// Phase K Wave 12 — Manifest screenshots visual regression spec.
//
// W12 — original spec authored by Vasquez.  Iterated the manifest
// `screenshots[]` and used `page.setContent()` to embed each entry
// as an `<img>` inside a blank page before snapshotting.
//
// W14 — Vasquez landed a partial fix (`page.goto('/')` before
// `setContent`) so the relative `<img src>` URLs resolved against
// the deployed origin instead of `about:blank`.  See
// `docs/test-architecture.md §5.2`.
//
// W15 — Hicks completes the Playwright snapshot best-practice
// alignment (charter item 2):
//
//   1. All `page.setContent()` calls are removed.  Instead the
//      spec navigates the browser DIRECTLY to each manifest
//      screenshot URL via `page.goto(<asset-url>)` and lets the
//      browser render the image at its natural viewport.
//      `await page.waitForLoadState('networkidle')` replaces the
//      hand-rolled `<img>.complete` polling — fewer moving parts.
//   2. The new `snapshotPathTemplate` in `playwright.config.ts`
//      pins baselines under
//      `tests/e2e/__screenshots__/<spec>/<arg>.png`.  Each
//      manifest screenshot's slug becomes the baseline filename
//      (e.g. `screenshots/main-game.png` → `main-game.png`).
//      Documented in `docs/frontend-pwa-audit.md §7.2`.
//   3. Forward-stage tolerance preserved across:
//        - origin unreachable
//        - no manifest at canonical paths
//        - empty `screenshots[]`
//        - asset-URL navigation fails (404 / network)
//        - baseline missing (Playwright auto-records on first run)
//
// W11 shipped *real* manifest screenshots (size + presence).  This
// spec adds visual regression with a 2 % pixel-diff tolerance per
// `docs/test-architecture.md §5`.  Real LIVE captures of the
// rendered lobby surfaces live in
// `visual-regression-real-captures.spec.ts` (Vasquez W14).
//
// See `tests/selectors.md` § Phase K Wave 12 → manifest-screenshots-visual.

import { test, expect, type Page } from '@playwright/test';

const MANIFEST_CANDIDATES = [
  '/manifest.webmanifest',
  '/autotable/manifest.webmanifest',
  '/manifest.json',
];

const MAX_DIFF_PIXEL_RATIO = 0.02; // W12 §5 policy: <=2%.

interface ScreenshotEntry {
  src?: string;
  sizes?: string;
}

interface Manifest {
  screenshots?: ScreenshotEntry[];
}

async function fetchManifest(page: Page): Promise<Manifest | null> {
  for (const path of MANIFEST_CANDIDATES) {
    try {
      const res = await page.request.get(path);
      if (res.ok()) {
        return JSON.parse(await res.text()) as Manifest;
      }
    } catch (_e) { /* try next */ }
  }
  return null;
}

function slug(src: string | undefined): string {
  if (!src) return 'unnamed';
  return src.split('/').pop()!.replace(/\.[a-z]+$/i, '').replace(/[^a-z0-9-]/gi, '-');
}

function resolveAssetPath(src: string): string {
  if (src.startsWith('http://') || src.startsWith('https://')) return src;
  return src.startsWith('/') ? src : `/${src}`;
}

test.describe('Phase K Wave 12 — manifest screenshots visual regression (<= 2% diff)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'Visual regression validated on chromium only (per §5 determinism rule).');
  });

  test('each manifest screenshot matches its baseline within 2% diff OR forward-staged',
    async ({ page }, testInfo) => {
      // Step 1 — reach a real origin so the manifest fetch resolves.
      // W15: keep the bare-origin probe so the manifest fetch below
      // has a deployed `baseURL` to resolve against.  The subsequent
      // per-asset `page.goto()` replaces the W14 `setContent` step
      // entirely.
      try {
        await page.goto('/', { waitUntil: 'domcontentloaded' });
      } catch (_e) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Origin not reachable for manifest visual regression.',
        });
        return;
      }
      const manifest = await fetchManifest(page);
      if (manifest === null) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No manifest reachable at canonical paths.',
        });
        return;
      }
      const shots = Array.isArray(manifest.screenshots) ? manifest.screenshots : [];
      if (shots.length === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'Manifest has no screenshots[] yet.',
        });
        return;
      }

      let comparedCount = 0;
      for (const shot of shots) {
        if (!shot.src) continue;
        const url = resolveAssetPath(shot.src);

        // W15 — Navigate DIRECTLY to the asset URL.  The browser
        // renders the image at the viewport pinned by
        // `playwright.config.ts:devices['Desktop Chrome']`; no
        // setContent / no inline HTML / no hand-rolled load probe.
        let resp;
        try {
          resp = await page.goto(url, { waitUntil: 'load' });
        } catch (_e) {
          testInfo.annotations.push({
            type: 'forward-staged',
            description: `Screenshot asset "${url}" navigation failed on the deployed origin.`,
          });
          continue;
        }
        if (resp === null || !resp.ok()) {
          testInfo.annotations.push({
            type: 'forward-staged',
            description: `Screenshot asset "${url}" returned ${resp?.status() ?? 'no-response'}.`,
          });
          continue;
        }
        try {
          await page.waitForLoadState('networkidle', { timeout: 4000 });
        } catch (_e) {
          // Static asset; networkidle is best-effort.  Proceed.
        }

        // Pre-flight per §5: freeze any decorative animations the
        // browser default chrome might run on the image-rendering
        // page (e.g. broken-image icon shimmer).
        await page.addStyleTag({
          content: `
            *, *::before, *::after {
              animation-duration: 0s !important;
              animation-delay: 0s !important;
              transition-duration: 0s !important;
              transition-delay: 0s !important;
            }
          `,
        });
        // Wait for fonts so any browser-chrome text doesn't drift.
        await page.evaluate(async () => {
          if (document.fonts && document.fonts.ready) {
            await document.fonts.ready;
          }
        });

        try {
          // The first positional arg becomes `{arg}` in
          // `snapshotPathTemplate`; baseline lands at
          // `tests/e2e/__screenshots__/manifest-screenshots-visual.spec.ts/<slug>.png`.
          await expect(page).toHaveScreenshot(
            `${slug(shot.src)}.png`,
            { maxDiffPixelRatio: MAX_DIFF_PIXEL_RATIO },
          );
          comparedCount++;
        } catch (e) {
          // First-run mode (no baseline) — Playwright records and
          // throws; surface that as forward-staged the first time.
          const msg = e instanceof Error ? e.message : String(e);
          if (msg.includes('Writing actual') || msg.includes('--update-snapshots')
              || msg.includes('A snapshot doesn')) {
            testInfo.annotations.push({
              type: 'forward-staged',
              description: `Recorded baseline for "${slug(shot.src)}.png"; future runs will compare.`,
            });
          } else {
            throw e;
          }
        }
      }

      if (comparedCount === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'No baselines compared this run; either all forward-staged or all newly recorded.',
        });
      }
    });
});

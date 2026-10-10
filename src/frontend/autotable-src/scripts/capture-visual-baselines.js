#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 13 — Hicks (Frontend).
//
// Generates the visual-regression baselines for Vasquez's W12 spec
// `tests/e2e/manifest-screenshots-visual.spec.ts`.  Writes one PNG
// per manifest screenshot under
// `tests/e2e/__screenshots__/manifest-screenshots-visual.spec.ts/`
// (the location referenced by the W13 hand-off docs).
//
// The W12 spec is currently `setContent()`-only (no `page.goto()`),
// so when run in `--update-snapshots=all` mode the image src
// resolves against `about:blank` and never loads → the spec's
// `waitForFunction(naturalWidth > 0)` times out, falls through to
// the "forward-staged" branch, and no baselines are written.  We
// work around this by capturing the baselines directly via the
// Playwright runtime API: navigate to the preview URL first so the
// `<img>` element resolves against the served origin.
//
// Usage:
//
//   # 1. Start a preview server (vite preview or full backend):
//   nohup npx vite preview --host 127.0.0.1 --port 4173 \
//     --strictPort --outDir ../autotable > vite-preview.log 2>&1 &
//
//   # 2. Run this script:
//   node scripts/capture-visual-baselines.js
//
//   # 3. Commit the generated PNGs under
//   #    `tests/e2e/__screenshots__/manifest-screenshots-visual.spec.ts/`.
//
// The script is idempotent — re-runs overwrite the baselines.  The
// W14 hand-off updates `playwright.config.ts:snapshotPathTemplate`
// (Vasquez lane) so the spec actually compares against these paths,
// and adds a `page.goto()` to the spec so the setContent flow loads
// images correctly.  See `docs/frontend-pwa-audit.md §7`.

const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const BASE_URL = process.env.E2E_BASE_URL || 'http://127.0.0.1:4173/';
const VIEWPORT = { width: 1280, height: 720 };
const OUT_DIR = path.resolve(
  __dirname,
  '..',
  'tests',
  'e2e',
  '__screenshots__',
  'manifest-screenshots-visual.spec.ts',
);

function slug(src) {
  if (!src) return 'unnamed';
  return src.split('/').pop().replace(/\.[a-z]+$/i, '').replace(/[^a-z0-9-]/gi, '-');
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });

  const browser = await chromium.launch();
  const context = await browser.newContext({ viewport: VIEWPORT, baseURL: BASE_URL });
  const page = await context.newPage();

  // Navigate to the preview origin first so relative `<img src=…>`
  // URLs resolve against the served origin and not `about:blank`.
  await page.goto(BASE_URL, { waitUntil: 'domcontentloaded' });

  // Fetch the manifest via the same in-page network stack the spec
  // uses; falls through a small candidate list so the script works
  // against both the standalone `vite preview` (root manifest) and
  // the full backend (`/autotable/manifest.webmanifest`).
  const candidates = ['/manifest.webmanifest', '/autotable/manifest.webmanifest', '/manifest.json'];
  let manifest = null;
  for (const p of candidates) {
    try {
      const res = await page.request.get(p);
      if (res.ok()) {
        manifest = JSON.parse(await res.text());
        console.log(`[capture-visual-baselines] using ${p}`);
        break;
      }
    } catch (_) { /* try next */ }
  }
  if (manifest === null) {
    console.error(`[capture-visual-baselines] no manifest reachable; tried ${candidates.join(', ')}`);
    await browser.close();
    process.exit(1);
  }
  const shots = Array.isArray(manifest.screenshots) ? manifest.screenshots : [];
  if (shots.length === 0) {
    console.error('[capture-visual-baselines] manifest.screenshots[] is empty');
    await browser.close();
    process.exit(1);
  }

  // Freeze animations + wait for fonts so re-runs are deterministic
  // (mirrors the W12 spec's pre-flight from `docs/test-architecture.md §5`).
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
  await page.evaluate(async () => {
    if (document.fonts && document.fonts.ready) {
      await document.fonts.ready;
    }
  });

  let captured = 0;
  for (const shot of shots) {
    if (!shot.src) continue;
    const url = shot.src.startsWith('/') || shot.src.startsWith('http')
      ? shot.src
      : `/${shot.src}`;
    const name = `${slug(shot.src)}.png`;
    const outFile = path.join(OUT_DIR, name);

    await page.setContent(
      `<!doctype html><html><body style="margin:0;background:#000;">
         <img src="${url}" style="display:block;max-width:100%;height:auto;" />
       </body></html>`,
      { waitUntil: 'load', baseURL: BASE_URL },
    );
    try {
      await page.waitForFunction(
        () => {
          const img = document.querySelector('img');
          return !!img && img.complete && img.naturalWidth > 0;
        },
        { timeout: 5000 },
      );
    } catch (_) {
      console.warn(`[capture-visual-baselines] image did not load: ${url} — skipping`);
      continue;
    }

    await page.screenshot({ path: outFile, fullPage: false });
    captured++;
    console.log(`[capture-visual-baselines] wrote ${path.relative(process.cwd(), outFile)}`);
  }

  await browser.close();
  console.log(`[capture-visual-baselines] captured ${captured}/${shots.length} baseline(s) at ${OUT_DIR}`);
  if (captured === 0) {
    process.exit(1);
  }
}

main().catch(err => {
  console.error('[capture-visual-baselines] fatal:', err);
  process.exit(1);
});

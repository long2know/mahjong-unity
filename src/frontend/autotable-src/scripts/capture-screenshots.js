#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 11 — Real PWA manifest screenshot capture.
//
// Owned by Hicks (Frontend).  Replaces the three W10 placeholder
// PNGs (committed to img/screenshot-{lobby,table,mobile}.auto.png)
// with real captures of the running app:
//
//   • main-game.png             — 1024×768 landscape (lobby + table)
//   • spectator-commentary.png  —  768×1024 portrait  (mobile / narrow)
//   • tournament-dashboard.png  — 1024×768 landscape (tournaments tab)
//
// Captures land under `src/frontend/autotable-src/static/screenshots/`
// — committed (PNG files are deterministic on the same Playwright
// version) and copied to `dist/screenshots/` by `vite.config.ts:
// copyStaticAssets` so the W11 manifest's `screenshots[]` entries
// resolve at install time.
//
// Recipe (manual / once per release):
//
//   cd src/frontend/autotable-src
//   npm run build:vite
//   node scripts/capture-screenshots.js
//   git add static/screenshots/*.png
//
// CI is opt-in via the PWA Builder workflow (W11 hand-off) — we
// don't want every PR to regenerate the screenshots since Chromium
// version drift would flap the bytes.

const { chromium } = require('playwright');
const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const http = require('node:http');

const SRC_ROOT = path.resolve(__dirname, '..');
const DIST_ROOT = path.resolve(__dirname, '..', '..', 'autotable');
const OUT_DIR = path.resolve(SRC_ROOT, 'static', 'screenshots');
const PREVIEW_PORT = process.env.PREVIEW_PORT || 4174;
const PREVIEW_URL = `http://127.0.0.1:${PREVIEW_PORT}/`;

async function waitForServer(url, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await new Promise((resolve, reject) => {
        const req = http.get(url, res => {
          // Any response (including 404) means the server is up.
          res.resume();
          resolve(res.statusCode);
        });
        req.on('error', reject);
        req.setTimeout(2000, () => req.destroy(new Error('timeout')));
      });
      return true;
    } catch {
      await new Promise(r => setTimeout(r, 500));
    }
  }
  return false;
}

async function captureOne(browser, { url, viewport, outFile, prep }) {
  const ctx = await browser.newContext({ viewport, deviceScaleFactor: 1 });
  const page = await ctx.newPage();
  try {
    await page.goto(url, { waitUntil: 'load', timeout: 20000 });
    // Wait for the lobby skeleton to settle — body[lang] is set by
    // the i18n boot step.
    await page.waitForSelector('body[lang]', { timeout: 10000 }).catch(() => undefined);
    if (typeof prep === 'function') await prep(page);
    // One animation frame to let any tab-activate redraws settle.
    await page.waitForTimeout(750);
    await page.screenshot({ path: outFile, type: 'png', fullPage: false });
    console.log(`captured ${path.basename(outFile)} (${viewport.width}×${viewport.height})`);
  } finally {
    await ctx.close();
  }
}

async function main() {
  if (!fs.existsSync(DIST_ROOT) || !fs.existsSync(path.join(DIST_ROOT, 'index.html'))) {
    console.error(`[capture-screenshots] dist not found at ${DIST_ROOT} — run \`npm run build:vite\` first.`);
    process.exit(1);
  }
  fs.mkdirSync(OUT_DIR, { recursive: true });

  // Spawn `vite preview` against the built dist.  --strictPort fails
  // fast if the port is taken; we pick 4174 by default so the PWA
  // audit workflow's 4173 doesn't clash on local re-runs.
  console.log(`[capture-screenshots] starting vite preview on :${PREVIEW_PORT}`);
  const preview = spawn(
    path.resolve(SRC_ROOT, 'node_modules', '.bin', 'vite'),
    ['preview', '--host', '127.0.0.1', '--port', String(PREVIEW_PORT), '--strictPort', '--outDir', DIST_ROOT],
    { cwd: SRC_ROOT, stdio: ['ignore', 'pipe', 'pipe'] }
  );
  preview.stdout.on('data', d => process.stdout.write(`[preview] ${d}`));
  preview.stderr.on('data', d => process.stderr.write(`[preview] ${d}`));

  let exitCode = 0;
  try {
    const up = await waitForServer(PREVIEW_URL, 30000);
    if (!up) throw new Error(`preview server didn't come up at ${PREVIEW_URL}`);

    const browser = await chromium.launch({ headless: true });
    try {
      // 1. Main game view — 1024×768 landscape, lobby tab active.
      await captureOne(browser, {
        url: PREVIEW_URL,
        viewport: { width: 1024, height: 768 },
        outFile: path.join(OUT_DIR, 'main-game.png'),
        prep: async page => {
          // Ensure My Game tab is selected (the lobby default).
          const myGameTab = page.locator('#lobby-my-game-tab');
          if (await myGameTab.count() > 0) await myGameTab.click({ force: true }).catch(() => undefined);
        },
      });

      // 2. Spectator + commentary — 768×1024 narrow / portrait,
      //    activate the public games tab where spectatable tables
      //    live; this is the same view the `?action=spectate`
      //    shortcut lands users on.
      await captureOne(browser, {
        url: PREVIEW_URL,
        viewport: { width: 768, height: 1024 },
        outFile: path.join(OUT_DIR, 'spectator-commentary.png'),
        prep: async page => {
          const pubTab = page.locator('#lobby-public-games-tab');
          if (await pubTab.count() > 0) await pubTab.click({ force: true }).catch(() => undefined);
        },
      });

      // 3. Tournament dashboard — 1024×768 landscape, tournaments
      //    tab active (the `?action=tournament` shortcut landing).
      await captureOne(browser, {
        url: PREVIEW_URL,
        viewport: { width: 1024, height: 768 },
        outFile: path.join(OUT_DIR, 'tournament-dashboard.png'),
        prep: async page => {
          const tournTab = page.locator('#lobby-tournaments-tab');
          if (await tournTab.count() > 0) await tournTab.click({ force: true }).catch(() => undefined);
        },
      });
    } finally {
      await browser.close();
    }
  } catch (err) {
    console.error('[capture-screenshots] FAILED', err.message);
    exitCode = 1;
  } finally {
    preview.kill('SIGTERM');
    await new Promise(r => setTimeout(r, 250));
    if (!preview.killed) preview.kill('SIGKILL');
  }

  process.exit(exitCode);
}

main().catch(err => {
  console.error('[capture-screenshots] crashed', err);
  process.exit(1);
});

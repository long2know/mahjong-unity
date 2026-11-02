#!/usr/bin/env node
/* eslint-disable */
// Phase K Wave 14 — Hicks (Frontend).
//
// REAL visual-regression captures.  The Wave-13 script captured
// the manifest `screenshots[]` assets themselves (the small
// placeholder PNGs referenced from `manifest.webmanifest`) — not
// the actual rendered surfaces of the app.  W14 replaces those
// placeholders with screenshots of the live lobby surfaces, taken
// against a Vite preview server.
//
// What each baseline captures
// ---------------------------
//
//   main-game.png            — `/` cold load.  The lobby is fully
//                              rendered (settings strip, seat panel,
//                              public-games tab, leaderboard tab,
//                              tournaments tab).  This is the user-
//                              facing "main game" entry surface
//                              before any table is joined.
//
//   spectator-commentary.png — `/` with `?action=spectate` consumed
//                              by the action-router.  The router
//                              activates the public-games tab so
//                              the spectatable-games catalogue is
//                              the visible surface.  Without a
//                              backend the list renders its empty
//                              "No public games right now" state,
//                              which IS the real spectator entry
//                              point users hit when no games are
//                              live.
//
//   tournament-dashboard.png — `/` with `?action=tournament`.  The
//                              tournaments module lazy-loads on tab
//                              activation; with the backend absent
//                              it renders its "Coming soon"
//                              placeholder, which IS the real
//                              tournament-dashboard surface when no
//                              tournaments exist.
//
// Why "real" matters
// ------------------
// The W13 baselines were the static `static/screenshots/*.png`
// assets — solid-colour placeholders that could never trip a
// regression even if the entire lobby graph changed.  The W14
// baselines screenshot the actual rendered DOM, so any visible
// regression to the lobby chrome / typography / colour tokens
// will produce a pixel-diff against these baselines.
//
// Methodology notes
// -----------------
// • Viewport: 1280×720 (matches the W13 capture viewport so the
//   spec's `toHaveScreenshot()` machinery doesn't trip on a
//   resolution swap).
// • Animations frozen via `prefers-reduced-motion`-equivalent
//   inline stylesheet injection (mirrors the W13 script).
// • `await page.waitForLoadState('networkidle')` is used so the
//   lazy-import chunks for `?action=spectate` / `?action=tournament`
//   have time to land before the screenshot is taken.
// • Backend-shaped fetches are gracefully degraded by the
//   feature-detect paths in `tournaments.ts` / `lobby.ts`; no
//   mocking is needed for the empty-state captures.
//
// Usage
// -----
//
//   # 1. Build the frontend:
//   npm run build:vite
//
//   # 2. Start a preview server (detached):
//   nohup npx vite preview --host 127.0.0.1 --port 4173 \
//     --strictPort --outDir ../autotable > vite-preview.log 2>&1 &
//   PREVIEW_PID=$!
//
//   # 3. Run this script:
//   E2E_BASE_URL=http://127.0.0.1:4173/ node scripts/capture-real-surfaces.js
//
//   # 4. Stop the preview:
//   kill "$PREVIEW_PID"
//
// The script overwrites the W13 baselines under
// `tests/e2e/__screenshots__/manifest-screenshots-visual.spec.ts/`
// so the W14 spec re-run (after the Vasquez setContent fix lands)
// reads the W14 real surfaces.

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

const SURFACES = [
  {
    name: 'main-game.png',
    url: '/',
    description: 'Main game surface — lobby panel open on the default "My Game" tab (settings + seat picker + quick-match).',
    // Default lobby tab IS "My Game" — no extra click needed once
    // the lobby panel is forced open.
    activateTab: '#lobby-my-game-tab',
  },
  {
    name: 'spectator-commentary.png',
    url: '/?action=spectate',
    description: 'Spectator entry — public-games tab activated by action-router.',
    // Belt-and-braces: explicitly click the public-games tab after
    // the action-router has dispatched.  The router's click race
    // with the lobby tab binding can leave the active CSS class on
    // the tab but not the pane; an explicit click after settle
    // forces the pane swap so the screenshot reflects the
    // user-facing surface, not the half-activated transient state.
    activateTab: '#lobby-public-games-tab',
  },
  {
    name: 'tournament-dashboard.png',
    url: '/?action=tournament',
    description: 'Tournament dashboard — tournaments tab activated by action-router.',
    activateTab: '#lobby-tournaments-tab',
  },
];

async function capture(page, surface) {
  const fullUrl = new URL(surface.url, BASE_URL).toString();
  console.log(`[capture-real-surfaces] ${surface.name} ← ${fullUrl}`);
  await page.goto(fullUrl, { waitUntil: 'domcontentloaded' });

  // Inject an animations-off stylesheet so re-runs are byte-stable.
  await page.addStyleTag({
    content: `
      *, *::before, *::after {
        animation-duration: 0s !important;
        animation-delay: 0s !important;
        transition-duration: 0s !important;
        transition-delay: 0s !important;
      }
      .cursor { display: none !important; }
    `,
  });

  // Wait for fonts so antialiased glyphs render before the snapshot.
  await page.evaluate(async () => {
    if (document.fonts && document.fonts.ready) {
      await document.fonts.ready;
    }
  });

  // Give the action-router's lazy dispatch + the lobby's settings-
  // restore + any feature-detect probes a chance to land before
  // the screenshot.  600 ms is comfortably long enough on a cold
  // preview server while staying short enough to keep runs fast.
  await page.waitForTimeout(600);
  try {
    await page.waitForLoadState('networkidle', { timeout: 4000 });
  } catch (_) {
    // No network idle in 4s — proceed anyway.  The lobby renders
    // synchronously and the lazy chunks have already had 600 ms.
  }

  // Open the lobby panel so the tab strip is visible.  The default
  // boot state is "lobby closed" with only `#lobby-toggle` visible;
  // without opening the panel the screenshot just shows the table
  // background and the toggle chip.  We force the lobby open via
  // `.lobby-open` directly so the screenshot captures the user-
  // visible surface AFTER an explicit "open lobby" click.
  //
  // We also hard-hide any other overlays that boot-time modules
  // mount (`#magic-link-landing`, `#tour-overlay`, etc.) so they
  // don't intercept the tab clicks below.  These overlays are
  // `aria-hidden` in their dormant state but CSS-wise still cover
  // the viewport — fine in production (no click target above them
  // since the user has to take an action to surface them) but a
  // hostile environment for an automated tab-click in a capture.
  await page.evaluate(() => {
    const panel = document.getElementById('lobby-panel');
    if (panel !== null) panel.classList.add('lobby-open');
    const overlayIds = ['tour-overlay', 'magic-link-landing', 'signin-modal', 'signin-modal-backdrop', 'profile-page', 'avatar-migration-modal'];
    for (const id of overlayIds) {
      const el = document.getElementById(id);
      if (el !== null) {
        el.style.display = 'none';
        el.style.pointerEvents = 'none';
      }
    }
  });
  await page.waitForTimeout(150);

  // Force the target tab into its active pane so the screenshot
  // captures the intended surface, not the lobby default.  The
  // action-router already toggled the tab's `.lobby-tab-active`
  // class for the `?action=spectate` / `?action=tournament` URLs,
  // but the matching pane swap happens through the lobby's click
  // handler — a programmatic `.click()` after the panel is open
  // makes the pane state and the tab state consistent.
  if (surface.activateTab !== null) {
    try {
      await page.click(surface.activateTab, { timeout: 2000 });
      await page.waitForTimeout(400);
    } catch (err) {
      console.warn(`[capture-real-surfaces] could not activate ${surface.activateTab}: ${err && err.message}`);
    }
  }

  const outFile = path.join(OUT_DIR, surface.name);
  await page.screenshot({ path: outFile, fullPage: false });
  console.log(`[capture-real-surfaces] wrote ${path.relative(process.cwd(), outFile)}`);
}

async function main() {
  fs.mkdirSync(OUT_DIR, { recursive: true });

  const browser = await chromium.launch();
  const context = await browser.newContext({
    viewport: VIEWPORT,
    baseURL: BASE_URL,
    reducedMotion: 'reduce',
  });

  // Pre-set the onboarding-tour completion flag so the tour overlay
  // doesn't intercept pointer events / appear in the screenshot.  The
  // tour module reads `mahjong.tour.completed.v1` from localStorage
  // before scheduling itself.  We could also dismiss the overlay
  // imperatively per-page, but pre-setting the flag is cleaner +
  // matches the W13 baseline-capture behaviour.
  await context.addInitScript(() => {
    try { window.localStorage.setItem('mahjong.tour.completed.v1', 'true'); } catch (_) { /* ignore */ }
  });

  const page = await context.newPage();

  for (const surface of SURFACES) {
    try {
      await capture(page, surface);
    } catch (err) {
      console.error(`[capture-real-surfaces] failed ${surface.name}: ${err && err.message}`);
      await browser.close();
      process.exit(1);
    }
  }

  await browser.close();
  console.log(`[capture-real-surfaces] captured ${SURFACES.length} baseline(s) at ${OUT_DIR}`);
}

main().catch(err => {
  console.error('[capture-real-surfaces] fatal:', err);
  process.exit(1);
});

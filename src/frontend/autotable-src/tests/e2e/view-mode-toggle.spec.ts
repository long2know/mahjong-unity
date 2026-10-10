// #119 (Hicks / WP-D) — deterministic visual + structural view-mode gates.
//
// Standing criterion (Stephen): the table "MUST visibly behave like
// autotable" with "flat and perspective view options."  The P1-10 review
// found the prior spec asserted only camera *type* + tile/dice *counts* —
// a shader / material / geometry regression (black canvas, untextured
// tiles, a no-op camera swap, a NaN-poisoned geometry) would sail through.
//
// This spec closes that gap WITHOUT brittle full-frame screenshot
// baselines (WebGL rasterises differently across GPUs / the CI software
// renderer, which is exactly why the repo's committed-PNG visual-
// regression job is non-blocking).  Instead it reads the live WebGL
// framebuffer with `gl.readPixels` and asserts renderer-relative,
// deterministic pixel STATISTICS with generous thresholds:
//
//   • non-blank        — a healthy fraction of lit pixels (not a black or
//                        blown-out canvas);
//   • tile-rich        — many distinct colours (tile faces + backs +
//                        table), which collapses if materials/textures
//                        regress to a flat fill;
//   • mode-distinct    — perspective vs flat raster the SAME scene to
//                        MEASURABLY different frames (proves the camera
//                        swap is real, not a no-op);
//   • resize-correct   — the drawing buffer tracks the viewport and the
//                        frame stays healthy after a resize;
//   • variant-correct  — Changsha hides the Riichi centre HUD; both
//                        variants still lay a full wall + render richly;
//   • error-free       — no `computeBoundingSphere: Computed radius is
//                        NaN` (or other render console errors) — the #119
//                        `PlaneGeometry(0,0,0)` → NaN outline-thickness
//                        defect regression-guards here.
//
// The in-game view toggle is the `#perspective` checkbox (index.html),
// wired in game.ts:updateSettings() -> MainView.setPerspective(), which
// swaps the THREE camera between a PerspectiveCamera and a top-down
// OrthographicCamera (main-view.ts:makeCamera).  The `P` key and the
// settings-drawer mirror the same input.
//
// Real DOM/canvas only, against the backend serving the freshly-built
// bundle (C-8 harness).  No WS backdoors.  Screenshots are written as
// evidence artefacts, not asserted as flaky baselines.

import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// tests/e2e/ -> repo-root playtest-artifacts is five levels up (matches
// playtest-changsha.spec.ts).
const STAMP =
  process.env.HICKS_SHOT_STAMP ||
  new Date().toISOString().replace(/[:.]/g, '-');
const SHOT_DIR = path.resolve(
  __dirname,
  '../../../../../playtest-artifacts/screenshots',
  `hicks-view-mode-${STAMP}`,
);

// ── Deterministic health thresholds ──────────────────────────────
// Calibrated from real captures on both a hardware GPU and headless
// chromium; bands are deliberately wide so a legitimate render passes
// on any renderer while a genuine regression (blank / flat / no-op
// swap) fails.  Measured references (FOUR_PLAYER / CHANGSHA):
//   litFraction    perspective ~0.54-0.60, flat ~0.79-0.82
//   meanBrightness perspective ~83-95,      flat ~124
//   distinctColors ~126-167
const MIN_LIT = 0.12;          // below → canvas is (near) black
const MAX_LIT = 0.99;          // above → canvas is blown out / single fill
const MIN_MEAN_BRIGHT = 20;    // below → nothing is lit
const MAX_MEAN_BRIGHT = 236;   // above → white-out
const MIN_DISTINCT_COLORS = 40;// below → flat/untextured render
const MIN_MODE_LIT_DELTA = 0.05;    // perspective vs flat must differ
const MIN_MODE_GRID_DELTA = 0.10;   // >=10% of coarse grid cells differ

interface ViewProbe {
  cameraType: string;
  isPerspective: boolean;
  isOrthographic: boolean;
  tileCount: number;
  centerVisible: boolean;
  diceState: string | null;
  diceValues: [number, number] | null;
}

interface FrameStats {
  w: number;
  h: number;
  litFraction: number;
  meanBrightness: number;
  distinctColors: number;
  grid: number[]; // GRID_N*GRID_N mean-brightness signature (0..255)
}

// Read the live WebGL drawing buffer and compute deterministic pixel
// statistics + a coarse mean-brightness grid signature.  Runs entirely
// in-page against the exact renderer the user sees.  `mv.render()` is
// invoked first so the buffer holds a fresh frame before readPixels
// (the renderer is created without preserveDrawingBuffer).
function frameStatsInPage(gridN: number): FrameStats {
  const g: any = (window as any).game;
  const mv = g.mainView;
  mv.render();
  const gl = mv.renderer.getContext();
  const w = gl.drawingBufferWidth as number;
  const h = gl.drawingBufferHeight as number;
  const buf = new Uint8Array(w * h * 4);
  gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, buf);

  let lit = 0;
  let sum = 0;
  const colors = new Set<number>();
  const gridSum = new Float64Array(gridN * gridN);
  const gridCount = new Uint32Array(gridN * gridN);
  const n = w * h;
  for (let i = 0; i < n; i++) {
    const r = buf[i * 4];
    const gg = buf[i * 4 + 1];
    const b = buf[i * 4 + 2];
    const bright = (r + gg + b) / 3;
    sum += bright;
    if (bright > 40) lit++;
    colors.add(((r >> 4) << 8) | ((gg >> 4) << 4) | (b >> 4));
    const px = i % w;
    const py = (i / w) | 0;
    const gx = Math.min(gridN - 1, ((px / w) * gridN) | 0);
    const gy = Math.min(gridN - 1, ((py / h) * gridN) | 0);
    const gi = gy * gridN + gx;
    gridSum[gi] += bright;
    gridCount[gi]++;
  }
  const grid: number[] = [];
  for (let i = 0; i < gridN * gridN; i++) {
    grid.push(gridCount[i] ? +(gridSum[i] / gridCount[i]).toFixed(1) : 0);
  }
  return {
    w,
    h,
    litFraction: +(lit / n).toFixed(4),
    meanBrightness: +(sum / n).toFixed(2),
    distinctColors: colors.size,
    grid,
  };
}

// Fraction of coarse-grid cells whose mean brightness differs by more
// than `tol` between two frames — a renderer-relative measure of how
// much the projection changed the raster.
function gridDelta(a: number[], b: number[], tol = 12): number {
  const n = Math.min(a.length, b.length);
  let diff = 0;
  for (let i = 0; i < n; i++) if (Math.abs(a[i] - b[i]) > tol) diff++;
  return n ? diff / n : 0;
}

async function probe(page: Page): Promise<ViewProbe> {
  return page.evaluate(() => {
    const g: any = (window as any).game;
    const cam = g.mainView.camera;
    const center = g.objectView?.center ?? g.world?.objectView?.center ?? null;
    const dice = g.client.dice.get(0) ?? null;
    return {
      cameraType: cam.type,
      isPerspective: cam.isPerspectiveCamera === true,
      isOrthographic: cam.isOrthographicCamera === true,
      tileCount: g.world.things.size,
      centerVisible: center ? center.mesh.visible === true : false,
      diceState: dice ? dice.state : null,
      diceValues: dice ? dice.dice : null,
    };
  });
}

async function frameStats(page: Page, gridN = 12): Promise<FrameStats> {
  return page.evaluate(frameStatsInPage, gridN);
}

// Drive the centre canvas to paint the two dice pips so they are visible
// (FOUR_PLAYER only — Changsha hides the centre).  Exercises the exact
// Center.drawDice() path a live roll uses; set directly for a
// deterministic capture (no setTimeout reset race).
async function paintDice(page: Page): Promise<void> {
  await page.evaluate(() => {
    const g: any = (window as any).game;
    const center = g?.objectView?.center ?? g?.world?.objectView?.center;
    if (!center) return;
    center.diceInfo = { dice: [3, 4], state: 'rolled' };
    center.shouldDrawDice = true;
    center.dirty = true;
    center.draw();
  });
  await page.waitForTimeout(150);
}

// Flip the canonical `#perspective` input and dispatch the `change` the
// game listens for (game.ts:setupEvents).  Resolves once MainView has
// swapped the camera to the expected projection.
async function setPerspective(page: Page, on: boolean): Promise<void> {
  await page.evaluate((want) => {
    const cb = document.getElementById('perspective') as HTMLInputElement | null;
    if (cb === null) throw new Error('#perspective toggle not found');
    cb.checked = want;
    cb.dispatchEvent(new Event('change', { bubbles: true }));
  }, on);
  await page.waitForFunction(
    (want) => {
      const g: any = (window as any).game;
      const cam = g?.mainView?.camera;
      if (!cam) return false;
      return want ? cam.isPerspectiveCamera === true : cam.isOrthographicCamera === true;
    },
    on,
    { timeout: 10_000 },
  );
  // Let a couple of frames paint under the new projection.
  await page.waitForTimeout(250);
}

// Mount the standalone local-deal renderer (upstream autotable's local
// table path — NOT a WS backdoor) and lay a full wall for `variant`.
// A non-empty query string flips index.ts into bootstrapGame(); omitting
// ?gameId= keeps us in local-deal mode (ClientUi.start() only opens a WS
// when a gameId is present), so no server snapshot reconciles the tiles
// away.  The bundle + assets are still served by the Production-CSP backend
// under test.
//
// Variant plumbing differs by design:
//   • FOUR_PLAYER — the upstream relay local deal is live, so `world.deal`
//     authors the wall AND flips ObjectView via updateConditions→setVariant
//     (centre HUD stays visible for Riichi).
//   • CHANGSHA — server-authoritative: `world.deal` is INERT (it early-returns
//     at the FE-1 `blocksLocalDeal` gate), so it can neither author the scene
//     NOR flip ObjectView into Changsha.  The centre-HUD hide therefore has to
//     come from the product's real Changsha entry: declaring `?variant=changsha`
//     makes the ObjectView ctor (readVariantFromUrl → setVariant) hide the
//     Riichi centre HUD + skip the stick-tray at first paint, while the World
//     ctor lays the canonical 108-tile Changsha wall (Conditions.initial() ===
//     CHANGSHA).  No local deal is emitted (it would be a no-op).
async function mountAndDeal(page: Page, variant: 'FOUR_PLAYER' | 'CHANGSHA'): Promise<void> {
  const url = variant === 'CHANGSHA' ? './?variant=changsha&seat=0' : './?seat=0';
  await page.goto(url, { waitUntil: 'domcontentloaded' });

  for (const sel of ['#tour-skip', '#onboarding-skip', '#lobby-close']) {
    const el = page.locator(sel);
    if (await el.isVisible().catch(() => false)) {
      await el.click({ force: true, timeout: 3000 }).catch(() => undefined);
    }
  }

  // The heavy three-renderer chunk mints this sentinel right after it
  // publishes window.game + game.start().
  await page
    .locator('[data-testid="three-renderer-ready"]')
    .waitFor({ state: 'attached', timeout: 90_000 });
  await page.waitForFunction(
    () => {
      const g: any = (window as any).game;
      return !!(g && g.world && g.client && g.mainView && g.mainView.camera);
    },
    undefined,
    { timeout: 30_000 },
  );

  if (variant === 'FOUR_PLAYER') {
    await page.evaluate(() => {
      const g: any = (window as any).game;
      g.world.seat = 0;
      g.world.deal('HANDS', { gameType: 'FOUR_PLAYER' });
    });
    // Let the deal's own 1 s dice-reset timer fire before we take control.
    await page.waitForTimeout(1300);
  } else {
    // Changsha: the canonical 108-tile wall is already laid by the World ctor
    // and the Riichi centre HUD is already hidden by the ctor's
    // setVariant(CHANGSHA).  Seat the local viewer (parity with FOUR_PLAYER;
    // no deal is emitted — it is inert here) and wait for the wall + hidden
    // centre to settle before probing.
    await page.evaluate(() => {
      const g: any = (window as any).game;
      g.world.seat = 0;
    });
    await page.waitForFunction(
      () => {
        const g: any = (window as any).game;
        const center =
          g?.objectView?.center ?? g?.world?.objectView?.center ?? null;
        return !!(
          g &&
          g.world &&
          g.world.things &&
          g.world.things.size > 100 &&
          center &&
          center.mesh.visible === false
        );
      },
      undefined,
      { timeout: 30_000 },
    );
  }
}

// Attach a render-error sink.  Any `Computed radius is NaN` (or generic
// THREE.* / WebGL) console error is captured so a test can assert the
// scene rendered cleanly.  Returns the live array.
function trackRenderErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(`PAGEERROR: ${e.message}`));
  return errors;
}

function assertHealthyFrame(stats: FrameStats, label: string): void {
  expect(stats.w, `${label}: drawing buffer width`).toBeGreaterThan(0);
  expect(stats.h, `${label}: drawing buffer height`).toBeGreaterThan(0);
  expect(
    stats.litFraction,
    `${label}: lit fraction ${stats.litFraction} — canvas is (near) blank/black`,
  ).toBeGreaterThan(MIN_LIT);
  expect(
    stats.litFraction,
    `${label}: lit fraction ${stats.litFraction} — canvas is blown out / single fill`,
  ).toBeLessThan(MAX_LIT);
  expect(
    stats.meanBrightness,
    `${label}: mean brightness ${stats.meanBrightness}`,
  ).toBeGreaterThan(MIN_MEAN_BRIGHT);
  expect(
    stats.meanBrightness,
    `${label}: mean brightness ${stats.meanBrightness}`,
  ).toBeLessThan(MAX_MEAN_BRIGHT);
  expect(
    stats.distinctColors,
    `${label}: distinct colours ${stats.distinctColors} — render looks flat/untextured`,
  ).toBeGreaterThanOrEqual(MIN_DISTINCT_COLORS);
}

test.describe('#119 view modes — deterministic visual + structural gates', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'WebGL view-mode gates validated on chromium only.',
    );
    fs.mkdirSync(SHOT_DIR, { recursive: true });
  });

  test('camera toggles perspective <-> flat with tiles + dice in both', async ({ page }) => {
    test.setTimeout(150_000);
    const errors = trackRenderErrors(page);
    await mountAndDeal(page, 'FOUR_PLAYER');

    // Default: perspective.
    const initial = await probe(page);
    expect(
      initial.isPerspective,
      `expected default PerspectiveCamera, got ${initial.cameraType}`,
    ).toBe(true);
    expect(initial.tileCount, 'expected a full tile wall to be dealt').toBeGreaterThan(100);
    expect(
      initial.centerVisible,
      'dice-bearing centre must be visible for a FOUR_PLAYER deal',
    ).toBe(true);
    expect(initial.diceState, 'dice must be rolled after the deal').toBe('rolled');
    expect(Array.isArray(initial.diceValues) && initial.diceValues.length === 2).toBe(true);

    // Toggle to flat (orthographic top-down).
    await setPerspective(page, false);
    const flat = await probe(page);
    expect(
      flat.isOrthographic,
      `expected OrthographicCamera after flat toggle, got ${flat.cameraType}`,
    ).toBe(true);
    expect(flat.isPerspective).toBe(false);
    expect(flat.tileCount).toBeGreaterThan(100);
    expect(flat.centerVisible).toBe(true);
    expect(flat.diceState).toBe('rolled');

    // Toggle back to perspective.
    await setPerspective(page, true);
    const restored = await probe(page);
    expect(
      restored.isPerspective,
      `expected PerspectiveCamera after toggling back, got ${restored.cameraType}`,
    ).toBe(true);
    expect(restored.isOrthographic).toBe(false);
    expect(restored.tileCount).toBeGreaterThan(100);

    expect(errors, `render console errors: ${errors.join(' | ')}`).toEqual([]);
  });

  test('perspective + flat frames are non-blank, tile-rich, and measurably distinct', async ({
    page,
  }) => {
    test.setTimeout(150_000);
    const errors = trackRenderErrors(page);
    await mountAndDeal(page, 'FOUR_PLAYER');

    await setPerspective(page, true);
    await paintDice(page);
    const persp = await frameStats(page);
    assertHealthyFrame(persp, 'perspective');
    await page
      .locator('#main canvas')
      .screenshot({ path: path.join(SHOT_DIR, 'four_player-perspective.png') })
      .catch(() => undefined);

    await setPerspective(page, false);
    await paintDice(page);
    const flat = await frameStats(page);
    assertHealthyFrame(flat, 'flat');
    await page
      .locator('#main canvas')
      .screenshot({ path: path.join(SHOT_DIR, 'four_player-flat.png') })
      .catch(() => undefined);

    // The two projections must raster the same scene to DIFFERENT frames
    // (a no-op camera swap — or a frozen renderer — collapses both deltas).
    const litDelta = Math.abs(persp.litFraction - flat.litFraction);
    const gDelta = gridDelta(persp.grid, flat.grid);
    expect(
      litDelta,
      `perspective vs flat lit-fraction delta ${litDelta.toFixed(3)} too small — camera swap may be a no-op`,
    ).toBeGreaterThanOrEqual(MIN_MODE_LIT_DELTA);
    expect(
      gDelta,
      `perspective vs flat grid delta ${gDelta.toFixed(3)} too small — projections look identical`,
    ).toBeGreaterThanOrEqual(MIN_MODE_GRID_DELTA);

    expect(errors, `render console errors: ${errors.join(' | ')}`).toEqual([]);

    // eslint-disable-next-line no-console
    console.log(
      `[view-mode #119] persp lit=${persp.litFraction} mean=${persp.meanBrightness} colors=${persp.distinctColors} | ` +
        `flat lit=${flat.litFraction} mean=${flat.meanBrightness} colors=${flat.distinctColors} | ` +
        `litΔ=${litDelta.toFixed(3)} gridΔ=${gDelta.toFixed(3)} shots=${SHOT_DIR}`,
    );
  });

  test('drawing buffer tracks the viewport on resize and frame stays healthy', async ({
    page,
  }) => {
    test.setTimeout(150_000);
    const errors = trackRenderErrors(page);
    await page.setViewportSize({ width: 1280, height: 900 });
    await mountAndDeal(page, 'FOUR_PLAYER');
    await setPerspective(page, true);

    const before = await frameStats(page);
    assertHealthyFrame(before, 'resize:before');

    // Shrink the viewport; MainView.updateViewport() reacts on the next
    // animation frame (game.ts:mainLoop -> update -> updateViewport).
    await page.setViewportSize({ width: 760, height: 560 });
    await page.waitForTimeout(600);
    const after = await frameStats(page);

    // The drawing buffer must follow the viewport down (at least one
    // dimension shrinks) while staying even + positive.
    expect(
      after.w < before.w || after.h < before.h,
      `resize did not shrink the drawing buffer: before ${before.w}x${before.h}, after ${after.w}x${after.h}`,
    ).toBe(true);
    expect(after.w % 2, 'render width must stay even').toBe(0);
    expect(after.h % 2, 'render height must stay even').toBe(0);

    // And the frame must still be a healthy render (not blanked by the
    // resize path).
    assertHealthyFrame(after, 'resize:after');

    expect(errors, `render console errors: ${errors.join(' | ')}`).toEqual([]);

    // eslint-disable-next-line no-console
    console.log(
      `[view-mode #119 resize] before=${before.w}x${before.h} after=${after.w}x${after.h} ` +
        `afterLit=${after.litFraction} afterColors=${after.distinctColors}`,
    );
  });

  test('Changsha variant: Riichi centre hidden, wall laid, render rich in both modes', async ({
    page,
  }) => {
    test.setTimeout(150_000);
    const errors = trackRenderErrors(page);
    await mountAndDeal(page, 'CHANGSHA');

    // Changsha hides the upstream Riichi point-stick trays + centre score
    // HUD (object-view.ts:setVariant) — the render must reflect that while
    // still laying a full wall.
    await setPerspective(page, true);
    const persp = await probe(page);
    expect(persp.isPerspective).toBe(true);
    expect(
      persp.centerVisible,
      'Changsha must hide the Riichi centre HUD',
    ).toBe(false);
    expect(persp.tileCount, 'Changsha must lay a full wall').toBeGreaterThan(100);
    const perspStats = await frameStats(page);
    assertHealthyFrame(perspStats, 'changsha:perspective');
    await page
      .locator('#main canvas')
      .screenshot({ path: path.join(SHOT_DIR, 'changsha-perspective.png') })
      .catch(() => undefined);

    await setPerspective(page, false);
    const flat = await probe(page);
    expect(flat.isOrthographic).toBe(true);
    expect(flat.centerVisible).toBe(false);
    const flatStats = await frameStats(page);
    assertHealthyFrame(flatStats, 'changsha:flat');
    await page
      .locator('#main canvas')
      .screenshot({ path: path.join(SHOT_DIR, 'changsha-flat.png') })
      .catch(() => undefined);

    const litDelta = Math.abs(perspStats.litFraction - flatStats.litFraction);
    expect(
      litDelta,
      `Changsha perspective vs flat lit-fraction delta ${litDelta.toFixed(3)} too small`,
    ).toBeGreaterThanOrEqual(MIN_MODE_LIT_DELTA);

    expect(errors, `render console errors: ${errors.join(' | ')}`).toEqual([]);
  });
});

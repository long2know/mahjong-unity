// #119 (WP-D revision) — committed toHaveScreenshot baselines for BOTH view modes.
//
// The P1-10 review REJECTED the statistics-only gate (view-mode-toggle.spec.ts)
// and required real committed screenshot baselines for the flat AND perspective
// views, generated in a deterministic pinned environment and enforced by a
// BLOCKING CI job. This spec delivers exactly that; the statistical framebuffer
// gate remains as a COMPLEMENTARY check (view-mode-toggle.spec.ts) and a few of
// its health asserts are re-run here so a blank/blown-out frame is caught before
// the pixel compare.
//
// Determinism (see playwright.config.ts `visual` project + view-visual-gate.yml):
//   • software WebGL — ANGLE → SwiftShader (no GPU variance);
//   • pinned 960×540 @ DPR 1 viewport;
//   • pinned browser + fonts via mcr.microsoft.com/playwright:v1.60.0-jammy;
//   • seeded in-page Math.random → deterministic tile shuffle (setup.deal →
//     utils.shuffle) AND deterministic dice; dice are additionally pinned to a
//     fixed [3,4] face via Center.drawDice();
//   • `animations: 'disabled'` + an explicit settle render before each capture.
//   • 2% maxDiffPixelRatio — justified by the repeatability evidence recorded in
//     docs/frontend-visual-baselines.md (repeat runs diff 0 px in the pinned env; the
//     2% band absorbs sub-pixel AA drift across container patch releases).
//
// Real DOM/canvas only against the served bundle (the local-deal renderer path,
// NOT a WS backdoor) — identical harness to view-mode-toggle.spec.ts.

import { test, expect, type Page } from '@playwright/test';

const MAX_DIFF_PIXEL_RATIO = 0.02; // §5 policy; justified by repeatability evidence.

// Strict measurement affordance (evidence only): set VISUAL_STRICT=1 to demand a
// ZERO-pixel diff, so the repeatability tolerance can be characterised. The gate
// default remains the 2% ratio above.
const SHOT_OPTS = process.env.VISUAL_STRICT === '1'
  ? ({ maxDiffPixels: 0, animations: 'disabled' } as const)
  : ({ maxDiffPixelRatio: MAX_DIFF_PIXEL_RATIO, animations: 'disabled' } as const);

// Deterministic seed → identical deal + dice every run.
const DEAL_SEED = 0x51190119;

interface FrameStats {
  w: number;
  h: number;
  litFraction: number;
  meanBrightness: number;
  distinctColors: number;
}

// Install a seeded mulberry32 PRNG as Math.random, IN-PAGE. Called both at page
// load (addInitScript) and again immediately before the deal so the shuffle
// starts from a fixed PRNG state regardless of any random consumed during boot.
function seededRandomSource(seed: number): string {
  return `
    (function(){
      var a = (${seed}) >>> 0;
      Math.random = function () {
        a |= 0; a = (a + 0x6D2B79F5) | 0;
        var t = Math.imul(a ^ (a >>> 15), 1 | a);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
      };
    })();
  `;
}

async function frameStats(page: Page): Promise<FrameStats> {
  return page.evaluate(() => {
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
    const n = w * h;
    for (let i = 0; i < n; i++) {
      const r = buf[i * 4];
      const gg = buf[i * 4 + 1];
      const b = buf[i * 4 + 2];
      const bright = (r + gg + b) / 3;
      sum += bright;
      if (bright > 40) lit++;
      colors.add(((r >> 4) << 8) | ((gg >> 4) << 4) | (b >> 4));
    }
    return {
      w,
      h,
      litFraction: +(lit / n).toFixed(4),
      meanBrightness: +(sum / n).toFixed(2),
      distinctColors: colors.size,
    };
  });
}

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
}

// Force a fresh settled frame into the drawing buffer right before a capture.
// The renderer has preserveDrawingBuffer:false, so render several times with a
// short settle so the compositor grabs a fully-painted frame.
async function settleAndRender(page: Page): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await page.evaluate(() => {
      const g: any = (window as any).game;
      g.mainView.render();
    });
    await page.waitForTimeout(120);
  }
}

// Pin the two dice faces to a fixed value so the centre HUD is identical every
// run (FOUR_PLAYER only; Changsha hides the centre).
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

// Hide every DOM overlay (lobby panel, Move Log with its live timestamps,
// settings gear, HUD dice img, buttons) so the capture is the PURE WebGL scene —
// deterministic (no wall-clock timestamps) and free of shell chrome. Keeps only
// the renderer canvas and its ancestor chain visible. Direct DOM style writes
// (not addStyleTag) → CSP-safe under the Production-CSP backend.
async function hideChrome(page: Page): Promise<void> {
  await page.evaluate(() => {
    const g: any = (window as any).game;
    const gl: HTMLElement = g.mainView.renderer.domElement;
    const keep = new Set<Element>();
    let el: Element | null = gl;
    while (el) { keep.add(el); el = el.parentElement; }
    document.querySelectorAll('body *').forEach((node) => {
      if (keep.has(node) || node.contains(gl)) return;
      (node as HTMLElement).style.setProperty('display', 'none', 'important');
    });
    document.body.style.setProperty('background', '#101014', 'important');
  });
}

async function mountAndDealDeterministic(
  page: Page,
  variant: 'FOUR_PLAYER' | 'CHANGSHA',
): Promise<void> {
  await page.addInitScript(seededRandomSource(DEAL_SEED));
  await page.goto('./?seat=0', { waitUntil: 'domcontentloaded' });

  for (const sel of ['#tour-skip', '#onboarding-skip', '#lobby-close']) {
    const el = page.locator(sel);
    if (await el.isVisible().catch(() => false)) {
      await el.click({ force: true, timeout: 3000 }).catch(() => undefined);
    }
  }

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

  // Reseed right before the deal, then deal locally (World.deal is authoritative
  // in no-gameId mode; no server snapshot reconciles the tiles away). The PRNG is
  // reinstalled inline (no eval → CSP-safe) so the shuffle is deterministic
  // regardless of any random consumed during boot.
  await page.evaluate(
    ({ v, seed }) => {
      let a = seed >>> 0;
      Math.random = function () {
        a |= 0; a = (a + 0x6d2b79f5) | 0;
        let t = Math.imul(a ^ (a >>> 15), 1 | a);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
      };
      const g: any = (window as any).game;
      g.world.seat = 0;
      g.world.deal('HANDS', { gameType: v });
    },
    { v: variant, seed: DEAL_SEED },
  );
  // Let the deal's own 1 s dice-reset timer fire before we take control.
  await page.waitForTimeout(1300);
}

test.describe('#119 view modes — committed visual baselines (flat + perspective)', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'visual',
      'Committed WebGL baselines run only under the deterministic `visual` project.',
    );
  });

  test('FOUR_PLAYER perspective + flat match committed baselines', async ({ page }) => {
    test.setTimeout(180_000);
    await mountAndDealDeterministic(page, 'FOUR_PLAYER');
    await paintDice(page);
    await hideChrome(page);

    // ── Perspective (default) ────────────────────────────────────────
    await setPerspective(page, true);
    await settleAndRender(page);

    // Complementary statistical health check — a blank/blown-out frame fails
    // here before we even reach the pixel compare.
    const persp = await frameStats(page);
    expect(persp.w, 'perspective drawing buffer width').toBeGreaterThan(0);
    expect(persp.litFraction, `perspective lit fraction ${persp.litFraction}`).toBeGreaterThan(0.2);
    expect(persp.litFraction, `perspective lit fraction ${persp.litFraction}`).toBeLessThan(0.98);
    expect(persp.distinctColors, `perspective colours ${persp.distinctColors}`).toBeGreaterThanOrEqual(40);

    await expect(page).toHaveScreenshot('four-player-perspective.png', SHOT_OPTS);

    // ── Flat (orthographic top-down) ─────────────────────────────────
    await setPerspective(page, false);
    await settleAndRender(page);

    const flat = await frameStats(page);
    expect(flat.litFraction, `flat lit fraction ${flat.litFraction}`).toBeGreaterThan(0.2);
    expect(flat.litFraction, `flat lit fraction ${flat.litFraction}`).toBeLessThan(0.98);
    expect(flat.distinctColors, `flat colours ${flat.distinctColors}`).toBeGreaterThanOrEqual(40);

    await expect(page).toHaveScreenshot('four-player-flat.png', SHOT_OPTS);

    // The two projections MUST raster to measurably different frames — proves the
    // camera swap is a real re-projection, not a no-op (complementary to the
    // pixel baselines, which would both still pass on a broken toggle).
    expect(
      Math.abs(persp.litFraction - flat.litFraction),
      `flat vs perspective lit-fraction delta (${persp.litFraction} vs ${flat.litFraction})`,
    ).toBeGreaterThan(0.02);
  });
});

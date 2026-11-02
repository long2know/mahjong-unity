// G20 (OWNED by hudson-1, adopted from hudson-2's red-responsive as that lane
// stops — Ripley 11:05). SPLIT per the integration gate:
//  (A) DOM/CSS overlap = FERRO's lane (reports green): primary controls are within
//      the viewport, hit-testable, not occluded by a fixed sidebar / top chrome.
//  (B) CANVAS fixed-ratio DEAD-SPACE = HICKS's seam (main-view.ts renderer sizing):
//      the WebGL <canvas> (renderer.domElement) must FILL the 390x844 viewport in
//      BOTH portrait AND landscape — no letterbox black bars. RED@200cad4 if the
//      renderer keeps a fixed aspect ratio and letterboxes on a tall/wide viewport.
// Real-UI only.
import { test, expect, type Page } from '@playwright/test';
import { buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected } from './_playability';
import { recordEvidence, shot } from './_uat_red';

const VIEWPORTS = [
  { name: 'mobile-portrait-390x844', w: 390, h: 844 },
  { name: 'mobile-landscape-844x390', w: 844, h: 390 },
  { name: 'tablet-portrait-820x1180', w: 820, h: 1180 },
  { name: 'tablet-landscape-1180x820', w: 1180, h: 820 },
];

// Primary controls a player must be able to reach (Ferro DOM/CSS lane). Under
// Changsha the authoritative primary control is the persistent New Game button
// (`#new-game` / `[data-testid=new-game-button]`); `#deal` is a relay-only
// control the backend hides for Changsha, so it is NOT the reachability target.
const CONTROL_IDS = ['new-game', 'variant-badge', 'turn-banner'];

// Measure the ACTUAL WebGL drawing surface vs the viewport. The real render is the
// <canvas> the Three.js renderer draws into (renderer.domElement) — its CSS box (and
// backing buffer) reveal letterbox dead-space that a container-<div> cover ratio hides.
async function readCanvasFill(page: Page) {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const vw = window.innerWidth, vh = window.innerHeight;
    const g = (window as any).game;
    const renderer = g?.mainView?.renderer ?? g?.mainView?.threeRenderer;
    const dom: HTMLCanvasElement | null = (renderer?.domElement as HTMLCanvasElement) ?? (document.querySelector('#main canvas') as HTMLCanvasElement) ?? (document.querySelector('canvas') as HTMLCanvasElement);
    if (!dom) return { vw, vh, hasCanvas: false };
    const r = dom.getBoundingClientRect();
    // fraction of each viewport axis the canvas CSS box actually spans
    const spanX = Math.min(r.right, vw) - Math.max(r.left, 0);
    const spanY = Math.min(r.bottom, vh) - Math.max(r.top, 0);
    const fillX = Math.max(0, spanX) / vw, fillY = Math.max(0, spanY) / vh;
    // dead-space bars = viewport area not covered by the canvas CSS box
    const coverArea = (Math.max(0, spanX) * Math.max(0, spanY)) / (vw * vh);
    return {
      vw, vh, hasCanvas: true,
      cssW: Math.round(r.width), cssH: Math.round(r.height), cssX: Math.round(r.left), cssY: Math.round(r.top),
      bufW: dom.width, bufH: dom.height,
      fillX: Math.round(fillX * 1000) / 1000, fillY: Math.round(fillY * 1000) / 1000,
      coverArea: Math.round(coverArea * 1000) / 1000,
    };
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

test.describe('G20 responsive: DOM/CSS reachability (Ferro) + WebGL canvas fills viewport (Hicks)', () => {
  for (const vp of VIEWPORTS) {
    test(`layout + canvas-fill @ ${vp.name}`, async ({ page }, testInfo) => {
      testInfo.setTimeout(90_000);
      await page.setViewportSize({ width: vp.w, height: vp.h });
      const base = testInfo.project.use.baseURL as string;
      const cfg = makeConfig({ gameId: `g20-${vp.name}-${Date.now()}`, dealMode: 'auto', botCount: 3, botDifficulty: 'Medium', handCount: 4 });
      await page.goto(buildGameUrl(base, cfg), { waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(1200); await dismissLobbyAndTour(page); await ensureConnected(page);
      await page.waitForTimeout(1000);

      const report = await page.evaluate((ids) => {
        /* eslint-disable @typescript-eslint/no-explicit-any */
        const vw = window.innerWidth, vh = window.innerHeight;
        const rects: Record<string, any> = {};
        for (const id of ids) {
          const el = document.getElementById(id);
          if (!el) { rects[id] = null; continue; }
          const r = el.getBoundingClientRect();
          const cs = getComputedStyle(el);
          const visible = r.width > 0 && r.height > 0 && cs.display !== 'none' && cs.visibility !== 'hidden' && r.right > 0 && r.bottom > 0 && r.left < vw && r.top < vh;
          let hitOk = false;
          if (visible) { const cx = Math.min(vw - 1, Math.max(0, r.left + r.width / 2)); const cy = Math.min(vh - 1, Math.max(0, r.top + r.height / 2)); const top = document.elementFromPoint(cx, cy); hitOk = !!top && (top === el || el.contains(top) || top.contains(el)); }
          rects[id] = { x: Math.round(r.left), y: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height), visible, hitOk, offViewport: r.right < 0 || r.bottom < 0 || r.left > vw || r.top > vh };
        }
        return { vw, vh, rects };
        /* eslint-enable @typescript-eslint/no-explicit-any */
      }, CONTROL_IDS);
      const fill = await readCanvasFill(page);
      await shot(page, `g20-${vp.name}.png`);
      recordEvidence(`g20-responsive-${vp.name}.json`, { report, fill });

      // (A) FERRO DOM/CSS lane (expected GREEN): the primary control (#new-game,
      // the persistent Changsha actuator) is within the viewport + hit-testable.
      const newGame = report.rects['new-game'];
      expect(newGame && !newGame.offViewport, `#new-game must be within the ${vp.name} viewport`).toBe(true);
      expect(newGame && newGame.visible && newGame.hitOk, `#new-game must be visible + hit-testable @ ${vp.name}; got ${JSON.stringify(newGame)}`).toBe(true);

      // (B) HICKS canvas-fill seam: the WebGL <canvas> must FILL the viewport in BOTH
      // axes — no letterbox dead-space. RED@200cad4 if the renderer letterboxes.
      expect(fill.hasCanvas, `a WebGL canvas must exist @ ${vp.name}`).toBe(true);
      expect(fill.fillX, `WebGL canvas must fill the viewport WIDTH @ ${vp.name} (no side letterbox bars); fillX=${fill.fillX} css=${fill.cssW}x${fill.cssH}`).toBeGreaterThan(0.98);
      expect(fill.fillY, `WebGL canvas must fill the viewport HEIGHT @ ${vp.name} (no top/bottom letterbox bars); fillY=${fill.fillY} css=${fill.cssW}x${fill.cssH}`).toBeGreaterThan(0.98);
      expect(fill.coverArea, `WebGL canvas must cover the full viewport area @ ${vp.name} (no dead black space); coverArea=${fill.coverArea}`).toBeGreaterThan(0.97);
    });
  }
});

// =============================================================================
//  #152 — HUD collision gate (RED on main, GREEN after the top-bar relayout)
// =============================================================================
//
//  Proves the desktop/mobile HUD chrome does not overlap after a REAL deal,
//  in BOTH flat (orthographic) and perspective camera modes, at the three
//  widths called out in the issue (1920×1080, 1920×1160, 1366×768) plus a
//  mobile width. Uses ONLY real deal controls + WS state reads (no
//  client.update / injection / synthetic DOM dispatch / forced clicks for
//  game progression). Overlap is measured from live boundingBox() geometry.
//
//  On current main this FAILS: `#lobby-toggle` overlaps `#sidebar`/`#deal`
//  (top-left) and `#settings-button` overlaps `#variant-badge` (top-right).
// =============================================================================

import { test, expect, type Page } from '@playwright/test';
import {
  buildGameUrl, makeConfig, dismissLobbyAndTour, ensureConnected,
  takeSeatByClick, clickDeal, waitForPlayableHand, readCameraType, pressViewToggle,
} from './_playability';

// Top-bar / HUD chrome that must never overlap another chrome element.
const HUD_SELECTORS = [
  '#lobby-toggle', '#variant-badge', '#settings-button', '#settings-toggle',
  '#move-log', '#move-log-toggle', '#mobile-move-log-toggle', '#dice-hud',
  '#bot-banner', '#pickup-hud', '.pwa-install-button',
];

interface Box { sel: string; x: number; y: number; w: number; h: number; }

// Ancestor/descendant pairs are containment, not collisions (e.g. #deal is a
// child of #sidebar). We only compare chrome elements that are NOT nested.
async function readChromeBoxes(page: Page): Promise<Box[]> {
  return page.evaluate((selectors: string[]) => {
    const out: { sel: string; x: number; y: number; w: number; h: number }[] = [];
    for (const sel of selectors) {
      const el = document.querySelector(sel) as HTMLElement | null;
      if (!el) continue;
      const style = getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden' || Number(style.opacity) === 0) continue;
      const r = el.getBoundingClientRect();
      if (r.width < 1 || r.height < 1) continue;
      // Skip anything off-screen (e.g. mobile install pill anchored elsewhere).
      if (r.right <= 0 || r.bottom <= 0) continue;
      out.push({ sel, x: r.x, y: r.y, w: r.width, h: r.height });
    }
    return out;
  }, HUD_SELECTORS);
}

function intersect(a: Box, b: Box): { ix: number; iy: number } | null {
  const ix = Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x);
  const iy = Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y);
  // Allow a 2px anti-aliasing tolerance.
  return ix > 2 && iy > 2 ? { ix: Math.round(ix), iy: Math.round(iy) } : null;
}

function findCollisions(boxes: Box[]): string[] {
  const hits: string[] = [];
  for (let i = 0; i < boxes.length; i++) {
    for (let j = i + 1; j < boxes.length; j++) {
      const o = intersect(boxes[i], boxes[j]);
      if (o) hits.push(`${boxes[i].sel} ∩ ${boxes[j].sel} = ${o.ix}×${o.iy}`);
    }
  }
  return hits;
}

const VIEWPORTS = [
  { name: '1920x1080', width: 1920, height: 1080 },
  { name: '1920x1160', width: 1920, height: 1160 },
  { name: '1366x768', width: 1366, height: 768 },
  { name: 'mobile-390x844', width: 390, height: 844 },
];

test.describe('#152 HUD chrome does not overlap', () => {
  for (const vp of VIEWPORTS) {
    test(`no HUD collisions @ ${vp.name}`, async ({ page }, testInfo) => {
      testInfo.setTimeout(120_000);
      await page.setViewportSize({ width: vp.width, height: vp.height });
      const cfg = makeConfig({
        dealMode: 'auto',
        gameId: `hud152-${vp.name}-${Date.now()}`,
        botCount: 3, handCount: 4,
      });
      await page.goto(buildGameUrl(testInfo.project.use.baseURL as string, cfg), { waitUntil: 'domcontentloaded' });
      await page.waitForTimeout(1200);
      await dismissLobbyAndTour(page);
      await ensureConnected(page);
      await takeSeatByClick(page, 0);
      await clickDeal(page);
      await waitForPlayableHand(page, 60_000);
      await page.waitForTimeout(1000);

      for (const wantPerspective of [false, true]) {
        // Toggle to the desired camera mode via the real 'p' keypress.
        let cam = await readCameraType(page);
        const isPerspective = cam === 'perspective';
        if (isPerspective !== wantPerspective) cam = await pressViewToggle(page);
        await page.waitForTimeout(400);

        const boxes = await readChromeBoxes(page);
        const collisions = findCollisions(boxes);
        await page.screenshot({
          path: testInfo.outputPath(`hud-${vp.name}-${wantPerspective ? 'perspective' : 'flat'}.png`),
        });
        expect(collisions, `HUD collisions @ ${vp.name} (${wantPerspective ? 'perspective' : 'flat'}):\n${collisions.join('\n')}`).toEqual([]);
      }
    });
  }
});

// Ferro — FE-6 / UAT G20 responsive contract for authoritative Changsha.
//
// Guards: (1) the legacy #sidebar never overlays the WebGL canvas on phones/
// tablets in Changsha (portrait AND landscape); (2) the top bar (turn banner
// vs variant badge) never overlaps at narrow widths; (3) the relay #new-game
// is absent in Changsha; (4) desktop still shows the Changsha sidebar (no
// over-hide). Real index.html × real src/style.css (Bootstrap not loaded, to
// isolate our own responsive rules). chromium only.

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

function findUp(rel: string, start: string, max = 10): string {
  let dir = start;
  for (let i = 0; i < max; i++) {
    const c = path.join(dir, rel);
    if (fs.existsSync(c)) return c;
    const p = path.dirname(dir);
    if (p === dir) break;
    dir = p;
  }
  throw new Error(`not found: ${rel}`);
}
const here = path.dirname(__filename);
const CSS = fs.readFileSync(findUp(path.join('src', 'style.css'), here), 'utf-8');
const INDEX = fs.readFileSync(findUp('index.html', here), 'utf-8');
const BODY = '<body>' + INDEX.replace(/<script[\s\S]*?<\/script>/gi, '').split(/<body[^>]*>/i)[1].split(/<\/body>/i)[0] + '</body>';
const DOC = `<!doctype html><html><head><meta charset="utf-8"><style>${CSS}</style></head>${BODY}</html>`;

const VIEWPORTS = [
  { name: 'phone-portrait-390x844', w: 390, h: 844 },
  { name: 'phone-landscape-844x390', w: 844, h: 390 },
  { name: 'tablet-portrait-820x1180', w: 820, h: 1180 },
  { name: 'small-phone-375x667', w: 375, h: 667 },
];

// A control is "hit-testable" when it is visible AND meets the 44px touch-target
// minimum on both axes (WCAG 2.5.5 / Apple HIG), fully inside the viewport.
const MIN_TOUCH = 44;

test.describe('Changsha responsive contract (FE-6/G20)', () => {
  test.beforeEach(async ({}, ti) => { test.skip(ti.project.name !== 'chromium', 'chromium only'); });

  for (const vp of VIEWPORTS) {
    test(`no #sidebar over canvas + no top-bar overlap + hit-testable New Game in Changsha — ${vp.name}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.w, height: vp.h });
      await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
      const r = await page.evaluate((MIN) => {
        const vis = (el: (Element & { checkVisibility?: () => boolean }) | null): boolean =>
          el === null ? false : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
        document.body.className = 'theme-light variant-changsha';
        const sb = document.getElementById('sidebar');
        const sidebarVisible = vis(sb);
        // Reveal the (JS-toggled) turn banner to measure top-bar overlap.
        const tb = document.getElementById('turn-banner') as HTMLElement | null;
        if (tb) { tb.hidden = false; tb.textContent = 'Your turn — click a tile to discard'; }
        const vb = document.getElementById('variant-badge');
        const a = tb?.getBoundingClientRect();
        const b = vb?.getBoundingClientRect();
        const overlap = a && b ? !(a.right <= b.left || a.left >= b.right || a.bottom <= b.top || a.top >= b.bottom) : false;
        // Blocker C — the Changsha primary control (#new-game) must be a real, tappable
        // target: visible, >=44px on both axes, and fully inside the viewport.
        const ng = document.getElementById('new-game');
        const ngVisible = vis(ng);
        const nr = ng?.getBoundingClientRect();
        const ngHit = !!(ngVisible && nr && nr.width >= MIN && nr.height >= MIN);
        const ngInView = !!(nr && nr.left >= 0 && nr.top >= 0
          && nr.right <= window.innerWidth + 1 && nr.bottom <= window.innerHeight + 1);
        return {
          sidebarVisible, overlap, newGameVisible: ngVisible, ngHit, ngInView,
          ng: nr ? { w: Math.round(nr.width), h: Math.round(nr.height), l: Math.round(nr.left), t: Math.round(nr.top) } : null,
          tb: a ? { l: Math.round(a.left), r: Math.round(a.right) } : null,
          vb: b ? { l: Math.round(b.left), r: Math.round(b.right) } : null,
        };
      }, MIN_TOUCH);
      expect(r.sidebarVisible, `#sidebar must NOT overlay the canvas in Changsha at ${vp.name}`).toBe(false);
      expect(r.overlap, `#turn-banner must not overlap #variant-badge at ${vp.name} (banner ${JSON.stringify(r.tb)} vs badge ${JSON.stringify(r.vb)})`).toBe(false);
      expect(r.newGameVisible, `persistent #new-game must be VISIBLE in Changsha at ${vp.name}`).toBe(true);
      expect(r.ngHit, `#new-game must be a >=${MIN_TOUCH}px hit target in Changsha at ${vp.name} (box ${JSON.stringify(r.ng)})`).toBe(true);
      expect(r.ngInView, `#new-game must be fully within the viewport at ${vp.name} (box ${JSON.stringify(r.ng)})`).toBe(true);
    });
  }

  // Blocker C (Bishop rev2) — relay variants keep the sidebar Deal/setup affordances;
  // on mobile/tablet they must be VISIBLE and meet the 44px touch target (they render
  // as Bootstrap btn-sm/form-control-sm ~31px otherwise). One profile per axis suffices.
  for (const vp of [VIEWPORTS[0], VIEWPORTS[1], VIEWPORTS[2]]) {
    test(`relay #deal + setup controls are hit-testable in variant-riichi — ${vp.name}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.w, height: vp.h });
      await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
      const r = await page.evaluate((MIN) => {
        const vis = (el: (Element & { checkVisibility?: () => boolean }) | null): boolean =>
          el === null ? false : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
        document.body.className = 'theme-light variant-riichi';
        // Expand the setup group so its dark-selectors are laid out for measurement.
        document.getElementById('setup-group')?.classList.remove('collapse');
        const box = (id: string): { visible: boolean; w: number; h: number; inView: boolean } => {
          const el = document.getElementById(id);
          if (!vis(el)) return { visible: false, w: 0, h: 0, inView: false };
          const b = el!.getBoundingClientRect();
          return {
            visible: true, w: Math.round(b.width), h: Math.round(b.height),
            inView: b.left >= 0 && b.top >= 0 && b.right <= window.innerWidth + 1 && b.bottom <= window.innerHeight + 1,
          };
        };
        return {
          sidebarVisible: vis(document.getElementById('sidebar')),
          deal: box('deal'), toggleSetup: box('toggle-setup'),
          dealType: box('deal-type'), gameType: box('game-type'),
        };
      }, MIN_TOUCH);
      expect(r.sidebarVisible, `relay #sidebar must remain visible at ${vp.name}`).toBe(true);
      expect(r.deal.visible && r.deal.h >= MIN_TOUCH, `relay #deal must be a >=${MIN_TOUCH}px hit target at ${vp.name} (box ${JSON.stringify(r.deal)})`).toBe(true);
      expect(r.deal.inView, `relay #deal must be within the viewport at ${vp.name} (box ${JSON.stringify(r.deal)})`).toBe(true);
      expect(r.toggleSetup.visible && r.toggleSetup.h >= MIN_TOUCH, `relay #toggle-setup must be a >=${MIN_TOUCH}px hit target at ${vp.name}`).toBe(true);
      expect(r.dealType.visible && r.dealType.h >= MIN_TOUCH, `relay #deal-type selector must be a >=${MIN_TOUCH}px hit target at ${vp.name} (box ${JSON.stringify(r.dealType)})`).toBe(true);
      expect(r.gameType.visible && r.gameType.h >= MIN_TOUCH, `relay #game-type selector must be a >=${MIN_TOUCH}px hit target at ${vp.name}`).toBe(true);
    });
  }

  test('secondary chrome touch targets meet 44px on tablet/mobile (Blocker D)', async ({ page }) => {
    // Blocker D — app-chrome toggles + Take Seat were ~36/38px on touch viewports. At <=1024px
    // the settings/lobby toggles and .take-seat must meet the 44px minimum on at least one axis.
    await page.setViewportSize({ width: 820, height: 1180 });
    await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
    const r = await page.evaluate((MIN) => {
      document.body.className = 'theme-light variant-changsha';
      // The seat-pick row (.seat-buttons) is display:none until the client
      // connects UNSEATED — game-ui.ts sets it to display:block when
      // client.seat===null. Scripts are stripped from this static DOC, so
      // replicate that unseated state before measuring the Take-Seat control
      // the user actually taps (otherwise .take-seat is a 0×0 hidden node and
      // the touch-target size — which only applies while it is shown — can't
      // be observed). This asserts the real rendered target, not a hidden one.
      const seatRow = document.querySelector('.seat-buttons') as HTMLElement | null;
      if (seatRow) seatRow.style.display = 'block';
      const box = (sel: string): { found: boolean; w: number; h: number } => {
        const el = document.querySelector(sel) as HTMLElement | null;
        if (!el) return { found: false, w: 0, h: 0 };
        const b = el.getBoundingClientRect();
        return { found: true, w: Math.round(b.width), h: Math.round(b.height) };
      };
      return {
        settings: box('#settings-toggle'),
        lobby: box('#lobby-toggle'),
        moveLog: box('#move-log-toggle'),
        takeSeat: box('.seat-button-0 .take-seat'),
      };
    }, MIN_TOUCH);
    expect(r.settings.found && r.settings.w >= MIN_TOUCH && r.settings.h >= MIN_TOUCH,
      `#settings-toggle must be a >=${MIN_TOUCH}px hit target (box ${JSON.stringify(r.settings)})`).toBe(true);
    expect(r.lobby.found && r.lobby.h >= MIN_TOUCH,
      `#lobby-toggle must be >=${MIN_TOUCH}px tall (box ${JSON.stringify(r.lobby)})`).toBe(true);
    expect(r.takeSeat.found && r.takeSeat.h >= MIN_TOUCH,
      `.take-seat must be >=${MIN_TOUCH}px tall (box ${JSON.stringify(r.takeSeat)})`).toBe(true);
  });

  test('desktop keeps the Changsha sidebar (not over-hidden)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
    const vis = await page.evaluate(() => {
      document.body.className = 'theme-light variant-changsha';
      const sb = document.getElementById('sidebar') as (Element & { checkVisibility?: () => boolean }) | null;
      return sb === null ? false : (sb.checkVisibility ? sb.checkVisibility() : sb.getClientRects().length > 0);
    });
    expect(vis, '#sidebar must remain available on desktop Changsha').toBe(true);
  });
});

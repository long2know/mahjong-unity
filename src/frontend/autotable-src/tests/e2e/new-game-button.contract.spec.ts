// Ferro — P0 persistent one-click New Game: a prominent, text-labeled primary
// button in the authoritative top chrome must be VISIBLE + hit-testable (not
// covered) + ≥44px touch target + keyboard/a11y + non-overlapping, in every
// Changsha phase and on desktop / 390×844 / landscape / tablet. Real index.html
// × real src/style.css (Bootstrap not loaded, to isolate our own rules).
// chromium only. Navigation is NOT tested here (Hicks's lane).

import { test, expect } from '@playwright/test';
import * as fs from 'node:fs';
import * as path from 'node:path';

function findUp(rel: string, start: string, max = 12): string {
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
const ROOT = path.dirname(findUp('index.html', path.dirname(__filename)));
const CSS = fs.readFileSync(path.join(ROOT, 'src', 'style.css'), 'utf-8');
const INDEX = fs.readFileSync(path.join(ROOT, 'index.html'), 'utf-8');
const BODY = '<body>' + INDEX.replace(/<script[\s\S]*?<\/script>/gi, '').split(/<body[^>]*>/i)[1].split(/<\/body>/i)[0] + '</body>';
const DOC = `<!doctype html><html><head><meta charset="utf-8"><style>${CSS}</style></head>${BODY}</html>`;

const NEIGHBOURS = ['#turn-banner', '#variant-badge', '#lobby-toggle', '#sidebar'];
const VIEWPORTS = [
  { name: 'desktop-1440x900', w: 1440, h: 900 },
  { name: 'phone-portrait-390x844', w: 390, h: 844 },
  { name: 'phone-landscape-844x390', w: 844, h: 390 },
  { name: 'tablet-portrait-820x1180', w: 820, h: 1180 },
];

// Serialisable probe of the New Game button + its neighbours in Changsha.
interface Probe {
  visible: boolean; tag: string; text: string; ariaLabel: string | null;
  w: number; h: number; hitSelf: boolean;
  overlaps: Array<{ sel: string; overlap: boolean }>;
}

test.describe('P0 persistent New Game button (top chrome)', () => {
  test.beforeEach(async ({}, ti) => { test.skip(ti.project.name !== 'chromium', 'chromium only'); });

  for (const vp of VIEWPORTS) {
    test(`visible + hit-testable + ≥44px + no overlap + a11y — Changsha @ ${vp.name}`, async ({ page }) => {
      await page.setViewportSize({ width: vp.w, height: vp.h });
      await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
      const r: Probe = await page.evaluate(({ bodyClass, neighbours }) => {
        const vis = (el: (Element & { checkVisibility?: () => boolean }) | null): boolean =>
          el === null ? false : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
        document.body.className = bodyClass;
        // Simulate a normal game-phase surface: the scriptless index.html leaves
        // JS-managed modals/drawers/chat rendered, but they are closed during
        // play. Hide high-z overlays (except the New Game button) so the hit-test
        // reflects reachability in seating/active/claim/disconnected/GameComplete.
        document.querySelectorAll('body *').forEach((el) => {
          const c = getComputedStyle(el);
          if ((c.position === 'fixed' || c.position === 'absolute') && parseInt(c.zIndex || '0', 10) >= 1000 && (el as HTMLElement).id !== 'new-game') {
            (el as HTMLElement).style.display = 'none';
          }
        });
        const btn = document.getElementById('new-game') as HTMLButtonElement;
        const rect = btn.getBoundingClientRect();
        const top = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2);
        const tb = document.getElementById('turn-banner') as HTMLElement | null;
        if (tb) { tb.hidden = false; tb.textContent = 'Your turn — click a tile to discard'; }
        const inter = (a: DOMRect, b: DOMRect): boolean => !(a.right <= b.left || a.left >= b.right || a.bottom <= b.top || a.top >= b.bottom);
        const overlaps = neighbours.map((sel) => {
          const el = document.querySelector(sel) as HTMLElement | null;
          return { sel, overlap: vis(el) ? inter(rect, el!.getBoundingClientRect()) : false };
        });
        return {
          visible: vis(btn), tag: btn.tagName, text: (btn.textContent || '').trim(),
          ariaLabel: btn.getAttribute('aria-label'), w: Math.round(rect.width), h: Math.round(rect.height),
          hitSelf: top === btn || btn.contains(top), overlaps,
        };
      }, { bodyClass: 'theme-light variant-changsha', neighbours: NEIGHBOURS });
      expect(r.visible, `New Game must be visible in Changsha @ ${vp.name}`).toBe(true);
      expect(r.hitSelf, `New Game must be hit-testable (nothing covering its center) @ ${vp.name}`).toBe(true);
      expect(r.w, `touch target width ≥44 @ ${vp.name}`).toBeGreaterThanOrEqual(44);
      expect(r.h, `touch target height ≥44 @ ${vp.name}`).toBeGreaterThanOrEqual(44);
      expect(r.tag, 'must be a real <button> (keyboard/a11y)').toBe('BUTTON');
      expect(r.text.toLowerCase(), 'must be text-labeled "New Game" (not icon-only)').toContain('new game');
      expect(r.ariaLabel, 'must carry an aria-label').toBeTruthy();
      for (const o of r.overlaps) {
        expect(o.overlap, `New Game must not overlap ${o.sel} @ ${vp.name}`).toBe(false);
      }
    });
  }

  // Structural contract for Hicks's persistent-New-Game binding (client-ui.ts
  // delegates a single document-level click on ANY [data-action="new-game"] to
  // the authoritative fresh-game path — debounced, single navigation). The DOM
  // contract Hicks's binding needs (and Ralph's relay-hidden-sidebar guard):
  //   • ≥1 [data-action="new-game"] element (delegation validly permits many —
  //     e.g. the legacy in-sidebar one and/or GameComplete may also carry it);
  //   • ≥1 PERSISTENT such control that lives OUTSIDE #sidebar and OUTSIDE the
  //     relay-only gate, is a real <button>, and is visible in Changsha — so it
  //     is never trapped in the relay-hidden sidebar;
  //   • no DUPLICATE id="new-game" (HTML validity), and GameComplete keeps its
  //     own distinct id.
  test('persistent [data-action=new-game] OUTSIDE #sidebar / relay-only gate; no duplicate id', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
    const r = await page.evaluate(() => {
      document.body.className = 'theme-light variant-changsha';
      const vis = (el: (Element & { checkVisibility?: () => boolean }) | null): boolean =>
        el === null ? false : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
      const actions = Array.from(document.querySelectorAll('[data-action="new-game"]'));
      // "Persistent" = outside the relay sidebar and outside the relay-only gate.
      const persistent = actions.filter((el) =>
        !el.closest('#sidebar') && !el.closest('.relay-only') && !(el as HTMLElement).classList.contains('relay-only'));
      const gc = document.getElementById('game-complete-new-game');
      return {
        idCount: document.querySelectorAll('#new-game').length,
        actionCount: actions.length,
        persistentCount: persistent.length,
        persistentVisible: persistent.some((el) => vis(el)),
        persistentIsButton: persistent.some((el) => el.tagName === 'BUTTON'),
        gcId: gc ? gc.id : null,
      };
    });
    expect(r.actionCount, 'at least one [data-action="new-game"] element (Hicks delegates on it)').toBeGreaterThanOrEqual(1);
    expect(r.persistentCount, 'at least one [data-action="new-game"] OUTSIDE #sidebar and the relay-only gate').toBeGreaterThanOrEqual(1);
    expect(r.persistentVisible, 'the persistent New Game control is visible in Changsha').toBe(true);
    expect(r.persistentIsButton, 'the persistent control is a real <button> (keyboard/a11y)').toBe(true);
    expect(r.idCount, 'no duplicate id="new-game" (0 or 1 occurrences)').toBeLessThanOrEqual(1);
    expect(r.gcId, 'GameComplete control keeps a distinct id — never a duplicate "new-game"').not.toBe('new-game');
  });

  test('stays reachable across phases (turn cue active, GameComplete modal, disconnected)', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
    const res = await page.evaluate(() => {
      const vis = (el: (Element & { checkVisibility?: () => boolean }) | null): boolean =>
        el === null ? false : (el.checkVisibility ? el.checkVisibility() : el.getClientRects().length > 0);
      document.body.className = 'theme-light variant-changsha';
      // Close JS-managed overlays (drawers/modals/chat) as during play.
      document.querySelectorAll('body *').forEach((el) => {
        const c = getComputedStyle(el);
        if ((c.position === 'fixed' || c.position === 'absolute') && parseInt(c.zIndex || '0', 10) >= 1000 && (el as HTMLElement).id !== 'new-game') {
          (el as HTMLElement).style.display = 'none';
        }
      });
      const btn = document.getElementById('new-game') as HTMLButtonElement;
      const hit = (): boolean => {
        const r = btn.getBoundingClientRect();
        const t = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
        return t === btn || btn.contains(t);
      };
      // active-hand / claim: turn cue shown.
      const tb = document.getElementById('turn-banner') as HTMLElement; tb.hidden = false; tb.textContent = 'Claim opportunity — 5s';
      const active = vis(btn) && hit();
      // GameComplete: a modal backdrop (z ~1050) covers the table — the button
      // (z 1300) must stay above it and clickable.
      const back = document.createElement('div');
      back.style.cssText = 'position:fixed;inset:0;z-index:1050;background:rgba(0,0,0,.5)';
      document.body.appendChild(back);
      const overModal = vis(btn) && hit();
      back.remove();
      // disconnected/reconnecting: chrome is phase-independent (fixed) — the
      // button does not depend on connection state.
      document.getElementById('server')?.classList.remove('connected');
      const disconnected = vis(btn) && hit();
      // disabled/aria-busy during navigation blocks activation (pointer-events).
      btn.disabled = true; btn.setAttribute('aria-busy', 'true');
      const busyPE = getComputedStyle(btn).pointerEvents;
      return { active, overModal, disconnected, busyPE };
    });
    expect(res.active, 'reachable while a turn/claim cue is shown').toBe(true);
    expect(res.overModal, 'reachable above the GameComplete modal backdrop').toBe(true);
    expect(res.disconnected, 'reachable while disconnected/reconnecting').toBe(true);
    expect(res.busyPE, 'aria-busy/disabled suppresses activation (pointer-events:none)').toBe('none');
  });

  test('relay modes keep their own Deal/new-game — persistent button hidden there', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await page.setContent(DOC, { waitUntil: 'domcontentloaded' });
    const vis = await page.evaluate(() => {
      document.body.className = 'variant-riichi';
      const btn = document.getElementById('new-game') as (Element & { checkVisibility?: () => boolean });
      return btn.checkVisibility ? btn.checkVisibility() : btn.getClientRects().length > 0;
    });
    expect(vis, 'persistent New Game is Changsha-only (relay keeps Deal/new-game)').toBe(false);
  });
});

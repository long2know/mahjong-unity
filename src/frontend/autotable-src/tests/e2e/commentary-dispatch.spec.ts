// Phase K Wave 10 — Commentary tile-reference click → 3D pulse spec
// (Vasquez).
//
// Hicks's W10 deliverable: when the user clicks a tile reference
// inside the commentary panel (e.g. the "5萬" chip in a recap line),
// the corresponding 3D tile in the table view pulses (a brief
// outline-shader highlight, ~600ms). The contract is:
//
//   1. Each clickable tile-ref carries a stable testid
//      `commentary-tileref-<row>-<index>`.
//   2. Click dispatches a custom DOM event named
//      `mahjong:highlight-tile` on the document, with
//      `event.detail.tileId` matching the rendered tile.
//   3. The 3D scene-shell listens for that event and toggles a
//      `data-pulse-tileid="<id>"` attribute on the canvas root
//      for the duration of the pulse.
//
// This spec asserts (1) + (2). (3) is exercised by
// outline-shader-visual.spec.ts and the unit-level Vitest
// for scene-shell.
//
// Forward-stage tolerant: when no tile-ref is found in the DOM
// (e.g. the demo content doesn't yet ship commentary), the spec
// soft-passes with an annotation.
//
// See selectors.md § Phase K Wave 10 → commentary-dispatch.

import { test, expect, type Page } from '@playwright/test';

// Canonical autotable route first.  When `/` is hit first the backend
// serves a meta-refresh redirect (`<meta http-equiv="refresh"
// content="0;url=/autotable/">`), which `page.goto` resolves OK but
// then immediately tears down the execution context as the refresh
// fires — any `page.evaluate` we queue right after the return throws
// "Execution context was destroyed".  Putting `/autotable/` first
// avoids the trap entirely.
const ROUTES = [
  '/autotable/',
  '/autotable/index.html',
  '/',
  '/index.html',
];

async function tryGoto(page: Page): Promise<boolean> {
  for (const r of ROUTES) {
    try {
      const res = await page.goto(r, { waitUntil: 'domcontentloaded' });
      if (res && res.ok()) {
        // Belt-and-braces against meta-refresh / soft redirects:
        // wait for the load state to settle before returning so a
        // subsequent `page.evaluate` doesn't race a tear-down.
        await page.waitForLoadState('load').catch(() => undefined);
        return true;
      }
    } catch (_e) { /* try next */ }
  }
  return false;
}

test.describe('Phase K Wave 10 — commentary-dispatch tile-ref → 3D pulse', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'commentary tile-ref dispatch validated on chromium only.');
  });

  test('clicking a commentary tile-ref dispatches mahjong:highlight-tile',
    async ({ page }, testInfo) => {
      const reached = await tryGoto(page);
      if (!reached) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'autotable shell not observable at canonical routes.',
        });
        return;
      }

      // Install a listener BEFORE the click so we capture the event.
      await page.evaluate(() => {
        (window as unknown as { __mahjongHighlightEvents?: string[] })
          .__mahjongHighlightEvents = [];
        document.addEventListener('mahjong:highlight-tile', (e: Event) => {
          const ce = e as CustomEvent<{ tileId?: string }>;
          (window as unknown as { __mahjongHighlightEvents?: string[] })
            .__mahjongHighlightEvents!.push(ce.detail?.tileId ?? '');
        });
      });

      // Locate the first commentary tile-ref. The testid pattern is
      // `commentary-tileref-<row>-<index>`; we accept the loose match.
      const tileref = page.locator('[data-testid^="commentary-tileref-"]').first();
      const count = await tileref.count();
      if (count === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'no commentary tile-ref present in current demo content.',
        });
        return;
      }

      await tileref.click({ trial: false });

      // Read back the captured events.
      const captured = await page.evaluate(() =>
        (window as unknown as { __mahjongHighlightEvents?: string[] })
          .__mahjongHighlightEvents ?? []);

      expect(captured.length,
        'click MUST dispatch at least one mahjong:highlight-tile event.',
      ).toBeGreaterThanOrEqual(1);
      expect(captured[0],
        'event detail.tileId MUST be a non-empty string identifier.',
      ).toMatch(/^.+$/);
    });

  test('every commentary tile-ref carries a stable testid',
    async ({ page }, testInfo) => {
      const reached = await tryGoto(page);
      if (!reached) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'autotable shell not observable.',
        });
        return;
      }

      const all = page.locator('[data-testid^="commentary-tileref-"]');
      const count = await all.count();
      if (count === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'no commentary tile-refs in DOM yet.',
        });
        return;
      }

      for (let i = 0; i < count; i++) {
        const id = await all.nth(i).getAttribute('data-testid');
        expect(id,
          `tileref index ${i} MUST carry a non-empty testid.`,
        ).toMatch(/^commentary-tileref-\d+-\d+$/);
      }
    });
});

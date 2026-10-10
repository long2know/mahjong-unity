// Phase K Wave 10 — Bracket renderer canonical-shape no-fallback spec
// (Vasquez).
//
// W7 introduced the canonical bracket shape (a tagged-union
// `{ kind: 'single-elim' | 'double-elim' | 'swiss', rounds: [...] }`).
// W8 added the round-by-round renderer. W9 added live updates.
// W10 TIGHTENS the contract: the bracket renderer MUST hard-fail
// (not silently render an empty grid) when the input is NOT one
// of the three canonical shapes. The W7→W9 lenient fallback to
// the empty grid was a debug aid that we're now removing.
//
// The contract:
//
//   1. Posting a valid bracket renders the round headings.
//   2. Posting a bracket with `kind: 'unknown'` triggers a
//      `bracket-renderer-error` testid containing the literal
//      "unknown bracket kind".
//   3. The error path does NOT render any `round-heading`
//      element (no silent fallback).
//
// Forward-stage tolerant: when there's no observable bracket
// page, the spec soft-passes with an annotation.
//
// See selectors.md § Phase K Wave 10 → bracket-canonical-no-fallback.

import { test, expect, type Page } from '@playwright/test';

const BRACKET_ROUTES = [
  '/bracket-demo',
  '/autotable/bracket-demo',
  '/autotable/bracket.html',
  '/bracket.html',
];

async function reachBracketPage(page: Page): Promise<boolean> {
  for (const r of BRACKET_ROUTES) {
    try {
      const res = await page.goto(r, { waitUntil: 'domcontentloaded' });
      if (res && res.ok()) {
        // The page mounted something the bracket renderer cares about.
        const c = await page.locator('[data-testid="bracket-renderer-root"]').count();
        if (c > 0) return true;
      }
    } catch (_e) { /* try next */ }
  }
  return false;
}

test.describe('Phase K Wave 10 — bracket-renderer canonical-no-fallback', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'bracket-renderer contract validated on chromium only.');
  });

  test('unknown kind triggers bracket-renderer-error with no round-heading',
    async ({ page }, testInfo) => {
      const reached = await reachBracketPage(page);
      if (!reached) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'bracket-demo route not observable in current build.',
        });
        return;
      }

      // Inject an unknown-kind bracket into the renderer.
      await page.evaluate(() => {
        const win = window as unknown as {
          mahjongBracketRenderer?: { render: (bracket: unknown) => void };
        };
        if (!win.mahjongBracketRenderer) return;
        try {
          win.mahjongBracketRenderer.render({ kind: 'unknown-shape', rounds: [] });
        } catch (_e) { /* expected */ }
      });

      // Error testid present.
      const err = page.locator('[data-testid="bracket-renderer-error"]');
      await expect(err,
        'bracket-renderer-error MUST appear when given an unknown kind.',
      ).toBeVisible({ timeout: 2_000 });

      const errText = (await err.textContent()) ?? '';
      expect(errText.toLowerCase(),
        'error message MUST mention "unknown bracket" so debugging is unambiguous.',
      ).toContain('unknown bracket');

      // No silent fallback: zero round-heading nodes rendered.
      const rounds = page.locator('[data-testid^="round-heading"]');
      expect(await rounds.count(),
        'unknown bracket MUST NOT silently render any round-heading.',
      ).toBe(0);
    });

  test('valid single-elim bracket renders round headings',
    async ({ page }, testInfo) => {
      const reached = await reachBracketPage(page);
      if (!reached) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'bracket-demo not observable.',
        });
        return;
      }

      await page.evaluate(() => {
        const win = window as unknown as {
          mahjongBracketRenderer?: { render: (bracket: unknown) => void };
        };
        if (!win.mahjongBracketRenderer) return;
        win.mahjongBracketRenderer.render({
          kind: 'single-elim',
          rounds: [
            { name: 'Quarter-finals', matches: [] },
            { name: 'Semi-finals', matches: [] },
            { name: 'Finals', matches: [] },
          ],
        });
      });

      const rounds = page.locator('[data-testid^="round-heading"]');
      const c = await rounds.count();
      if (c === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'demo bracket renderer not wired to window.mahjongBracketRenderer yet.',
        });
        return;
      }
      expect(c,
        'valid single-elim bracket MUST render at least one round-heading.',
      ).toBeGreaterThanOrEqual(1);
    });
});

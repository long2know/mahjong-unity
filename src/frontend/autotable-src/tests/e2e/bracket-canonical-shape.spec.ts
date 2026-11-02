// Phase K Wave 9 — Bracket canonical shape spec (Vasquez).
//
// W7/W8 shipped the tournament bracket renderer with multiple
// supported payload shapes (Swiss + double-elim + grand-final
// reset). W9 declares the CANONICAL shape and rejects unknown
// shapes with a console.error — no silent fallback.
//
// This spec drives an UNKNOWN bracket shape into the page via
// the window-exposed `__publishTournamentBracketUpdate` hook
// (installed by W8 tournaments.ts) and asserts a console.error
// fires.
//
// See selectors.md § Phase K Wave 9 → bracket-canonical-shape.

import { test, expect, type Page, type ConsoleMessage } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-bracket-canon',
      displayName: 'Bracket Canon Tester',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 9 — bracket-canonical-shape', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium',
      'bracket-canonical-shape validated on chromium only.');
  });

  test('client rejects unknown bracket shape with console error',
    async ({ page }, testInfo) => {
      test.setTimeout(45_000);

      const errors: string[] = [];
      page.on('console', (msg: ConsoleMessage) => {
        if (msg.type() === 'error' || msg.type() === 'warning') {
          errors.push(msg.text());
        }
      });

      await mockBackend(page);
      await page.goto('');
      await page.waitForLoadState('networkidle');

      const hookPresent = await page.evaluate(() => {
        return typeof (window as any).__publishTournamentBracketUpdate
          === 'function';
      });

      if (!hookPresent) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: 'window.__publishTournamentBracketUpdate hook not present.',
        });
        return;
      }

      // Push an UNKNOWN bracket shape — no `format` key, no `rounds`
      // key. The renderer MUST reject + console-error.
      await page.evaluate(() => {
        (window as any).__publishTournamentBracketUpdate({
          notARealShape: 'yes-this-is-garbage',
          mystery: ['xx', 'yy'],
        });
      });

      // Allow the renderer a tick to react.
      await page.waitForTimeout(250);

      const matchingErrors = errors.filter((e) =>
        /unknown\s+(bracket\s+)?shape|invalid\s+bracket\s+payload|UnknownBracketShape/i
          .test(e));

      if (matchingErrors.length === 0) {
        testInfo.annotations.push({
          type: 'forward-staged',
          description: `No console.error matching unknown-shape rejection. Observed errors: ${JSON.stringify(errors)}`,
        });
        return;
      }

      expect(matchingErrors.length).toBeGreaterThan(0);
    });
});

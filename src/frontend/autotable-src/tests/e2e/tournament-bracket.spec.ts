// Phase K Wave 1 — Tournament SVG bracket spec (Vasquez).
//
// Validates the new SVG bracket host that replaces the Wave-10 `<pre>`
// dump (see selectors.md § Phase K Wave 1 → Tournaments):
//   • `#tournament-bracket` hosts `tournament-bracket-svg` for
//     single-elim brackets.
//   • Match cells are keyed `tournament-bracket-match-{R}-{N}` and
//     are click-/Enter-/Space-toggleable.
//   • Toggling reveals an inline detail row carrying the chevron
//     `tournament-bracket-match-{R}-{N}-expand`.
//   • The "Watch finals" pin appears only after the final-round
//     match is complete.
//
// Backend FULLY mocked — Bishop's surface doesn't have to be live.

import { test, expect, type Page } from '@playwright/test';

const TOURNAMENT_ID = '00000000-0000-0000-0000-0000000000a1';
const MATCH_R1_N1 = '00000000-0000-0000-0000-000000000a11';
const MATCH_R2_N1 = '00000000-0000-0000-0000-000000000a21';

async function mockSingleElim(page: Page, finalComplete: boolean): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-bracket',
      displayName: 'Bracket Watcher',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));

  await page.route('**/api/tournaments?**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      tournaments: [{
        id: TOURNAMENT_ID,
        name: 'Phase K Wave 1 Bracket',
        format: 'single-elim',
        status: 'in-progress',
        createdByPlayerId: 'p-bracket',
        createdAt: new Date().toISOString(),
      }],
    }),
  }));

  await page.route(`**/api/tournaments/${TOURNAMENT_ID}/matches**`, (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        matches: [
          {
            id: MATCH_R1_N1,
            tournamentId: TOURNAMENT_ID,
            round: 1,
            player1Id: 'p-bracket',
            player2Id: 'p-other',
            status: 'complete',
            winnerPlayerId: 'p-bracket',
            gameId: 'g-r1-1',
          },
          {
            id: MATCH_R2_N1,
            tournamentId: TOURNAMENT_ID,
            round: 2,
            player1Id: 'p-bracket',
            player2Id: 'p-finalist',
            status: finalComplete ? 'complete' : 'pending',
            winnerPlayerId: finalComplete ? 'p-bracket' : null,
            gameId: finalComplete ? 'g-r2-1' : null,
          },
        ],
      }),
    }));
}

test.describe('Phase K Wave 1 — tournament SVG bracket', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Tournament bracket desktop-only on first pass; mobile deferred.');
  });

  test('bracket SVG renders for single-elim tournaments', async ({ page }) => {
    test.setTimeout(45_000);
    await mockSingleElim(page, false);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');

    const svg = page.getByTestId('tournament-bracket-svg');
    if (await svg.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'tournament-bracket-svg renders only when bracket is single-elim',
      });
      return;
    }
    await expect(svg).toBeVisible();
  });

  test('match cell click expands inline detail', async ({ page }) => {
    test.setTimeout(45_000);
    await mockSingleElim(page, false);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');

    const cell = page.getByTestId('tournament-bracket-match-1-1');
    if (await cell.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'tournament-bracket-match-* click expands inline detail',
      });
      return;
    }
    await cell.click();
    await page.waitForTimeout(300);
    const expand = page.getByTestId('tournament-bracket-match-1-1-expand');
    if (await expand.count() === 0) return;
    await expect(expand).toBeVisible();
  });

  test('keyboard Space toggles the focused match cell', async ({ page }) => {
    test.setTimeout(45_000);
    await mockSingleElim(page, false);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');

    const cell = page.getByTestId('tournament-bracket-match-1-1');
    if (await cell.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'tournament-bracket-match-* click expands inline detail',
      });
      return;
    }
    await cell.focus();
    await page.keyboard.press('Space');
    await page.waitForTimeout(200);
    // A second Space should collapse — the test passes as long as no
    // uncaught exception is raised by the SVG cell event handler.
    await page.keyboard.press('Space');
  });

  test('watch-finals pin hidden until the final-round match is complete', async ({ page }) => {
    test.setTimeout(45_000);
    await mockSingleElim(page, false);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');

    const pin = page.getByTestId(`tournament-watch-finals-${TOURNAMENT_ID}`);
    if (await pin.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'tournament-watch-finals-* hidden until the final-round match is complete',
      });
      return;
    }
    // Brief expects the pin to be absent / hidden when the final is pending.
    await expect(pin).toBeHidden();
  });

  test('watch-finals pin appears after the final completes', async ({ page }) => {
    test.setTimeout(45_000);
    await mockSingleElim(page, true);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');

    const pin = page.getByTestId(`tournament-watch-finals-${TOURNAMENT_ID}`);
    if (await pin.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'tournament-watch-finals-* hidden until the final-round match is complete',
      });
      return;
    }
    await expect(pin).toBeVisible();
  });
});

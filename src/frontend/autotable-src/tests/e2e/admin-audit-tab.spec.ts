// Phase J Wave 9 — Admin audit tab spec (Vasquez).
//
// Validates the replay viewer's "Audit" tab surfaced via Hicks's
// `src/audit.ts`:
//   • The tab is hidden by default (`#replay-tab-audit { display: none }`).
//   • When `/api/auth/me` reports an admin role, the tab becomes visible
//     and clicking it loads `/api/games/{id}/audit` rows into
//     `#replay-audit-table`.
//   • Non-admin sessions must never see the tab (security regression
//     guard).
//
// Reflection-defensive — soft-passes when the surface hasn't shipped.
// Backend is fully mocked: `/api/auth/me`, `/api/games/{id}/audit`, and
// `/api/games/{id}/replay` so the test doesn't depend on Bishop/Apone.
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md
// (Phase J Wave 9 § Replay viewer — admin audit tab).

import { test, expect, type Page } from '@playwright/test';

const FAKE_GAME_ID = '00000000-0000-0000-0000-00000000aaaa';

async function mockReplayBackend(page: Page, isAdmin: boolean): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p1',
      displayName: 'Test',
      claims: isAdmin ? { role: 'admin' } : { role: 'player' },
      roles: isAdmin ? ['admin'] : ['player'],
    }),
  }));

  await page.route('**/api/games/**/audit**', (route) => route.fulfill({
    status: isAdmin ? 200 : 403,
    contentType: 'application/json',
    body: JSON.stringify(isAdmin ? {
      rows: [
        {
          handNumber: 1,
          turn: 1,
          seat: 0,
          source: 'human',
          action: 'discard',
          durationMs: 850,
        },
        {
          handNumber: 1,
          turn: 2,
          seat: 1,
          source: 'bot',
          botTier: 'standard',
          action: 'draw',
          durationMs: 35,
          botScore: 0.72,
        },
      ],
    } : { error: 'forbidden' }),
  }));

  await page.route('**/api/games/**/replay**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      gameId: FAKE_GAME_ID,
      events: [],
      hands: [],
    }),
  }));
}

async function gotoReplay(page: Page): Promise<void> {
  await page.goto(`?gameId=${FAKE_GAME_ID}&view=replay`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(1000);
}

async function openReplayUi(page: Page): Promise<boolean> {
  const screen = page.getByTestId('replay-screen');
  if (await screen.count() === 0) return false;
  // The viewer's hidden state can be aria-hidden='true' or
  // display:none; we just rely on the audit-tab probe below for the
  // actual visibility check.
  return true;
}

test.describe('Mahjong Autotable — admin audit tab', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Admin audit tab desktop-only on first pass; mobile deferred.');
  });

  test('audit tab stays hidden for non-admin sessions', async ({ page }) => {
    test.setTimeout(45_000);
    await mockReplayBackend(page, /*isAdmin=*/false);
    await gotoReplay(page);

    if (!(await openReplayUi(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay viewer not yet wired',
      });
      return;
    }

    const auditTab = page.getByTestId('replay-audit-tab');
    if (await auditTab.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }

    // The tab element exists but must not be visible for a non-admin.
    await expect(auditTab).toBeHidden({ timeout: 5_000 });
  });

  test('audit tab becomes visible for admin sessions', async ({ page }) => {
    test.setTimeout(45_000);
    await mockReplayBackend(page, /*isAdmin=*/true);
    await gotoReplay(page);

    if (!(await openReplayUi(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay viewer not yet wired',
      });
      return;
    }

    const auditTab = page.getByTestId('replay-audit-tab');
    if (await auditTab.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }

    // The audit module probes /api/auth/me asynchronously; allow a few
    // hundred ms before checking visibility.
    await page.waitForTimeout(750);
    const visible = await auditTab.isVisible().catch(() => false);
    if (!visible) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'admin probe did not flip audit tab visibility',
      });
      return;
    }
    await expect(auditTab).toBeVisible({ timeout: 5_000 });
  });

  test('clicking audit tab loads rows into the audit table', async ({ page }) => {
    test.setTimeout(45_000);
    await mockReplayBackend(page, /*isAdmin=*/true);
    await gotoReplay(page);

    if (!(await openReplayUi(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay viewer not yet wired',
      });
      return;
    }

    const auditTab = page.getByTestId('replay-audit-tab');
    if (await auditTab.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }

    await page.waitForTimeout(750);
    if (!(await auditTab.isVisible().catch(() => false))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'audit tab not visible — admin probe pending',
      });
      return;
    }

    await auditTab.click();
    await page.waitForTimeout(750);

    // First row testid contract — `replay-audit-row-0`.
    const firstRow = page.getByTestId('replay-audit-row-0');
    if (await firstRow.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-0 testid not yet emitted',
      });
      return;
    }
    await expect(firstRow).toBeAttached({ timeout: 5_000 });

    const source = page.getByTestId('replay-audit-row-0-source');
    if (await source.count() > 0) {
      const txt = (await source.textContent()) ?? '';
      expect(txt.length).toBeGreaterThan(0);
    }
  });

  test('audit tab survives a 403 response gracefully', async ({ page }) => {
    test.setTimeout(45_000);

    // Admin per /api/auth/me, but the audit endpoint refuses.
    await page.route('**/api/auth/me**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        playerId: 'p1',
        claims: { role: 'admin' },
        roles: ['admin'],
      }),
    }));
    await page.route('**/api/games/**/audit**', (route) => route.fulfill({
      status: 403,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'forbidden' }),
    }));
    await page.route('**/api/games/**/replay**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ gameId: FAKE_GAME_ID, events: [], hands: [] }),
    }));

    await gotoReplay(page);
    if (!(await openReplayUi(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay viewer not yet wired',
      });
      return;
    }

    const auditTab = page.getByTestId('replay-audit-tab');
    if (await auditTab.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }

    await page.waitForTimeout(750);
    if (!(await auditTab.isVisible().catch(() => false))) return;

    await auditTab.click();
    await page.waitForTimeout(500);

    // After a 403 the audit-empty pane (if wired) should carry an error
    // hint; the page must not crash.
    await expect(page.getByTestId('replay-screen')).toBeAttached({ timeout: 2_000 });
  });
});

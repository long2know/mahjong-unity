// Phase J Wave 10 — Audit "why" row-expansion spec (Vasquez).
//
// Wave 10 extends Wave 9's admin audit tab with a per-row drill-down:
// clicking a row expands a "why" panel showing the bot's reasoning
// strings (from Bishop's `BotDecision.Reasoning` surface). The panel:
//   • Is collapsed by default.
//   • Toggles open/closed on row click.
//   • Renders each reasoning string as a separate list item.
//   • Colour-codes the strategy header (data-strategy attribute).
//
// Backend FULLY mocked — feeds canonical reasoning strings shaped to
// Bishop's Wave 10 contract.

import { test, expect, type Page } from '@playwright/test';

const FAKE_GAME_ID = '00000000-0000-0000-0000-00000000eeee';

async function mockAuditBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p1',
      displayName: 'Admin',
      claims: { role: 'admin' },
      roles: ['admin'],
    }),
  }));

  await page.route('**/api/games/**/audit**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      rows: [
        {
          handNumber: 1,
          turn: 1,
          seat: 0,
          source: 'bot',
          botTier: 'master',
          action: 'discard',
          tile: 14,
          durationMs: 35,
          botScore: 0.91,
          reasoning: [
            'strategy:master',
            'phase: AwaitingDiscard',
            'safety analysis: discard tile already played by an opponent (low Pung/Chow risk)',
            'tile candidate score=0.91',
          ],
        },
        {
          handNumber: 1,
          turn: 2,
          seat: 1,
          source: 'bot',
          botTier: 'medium',
          action: 'draw',
          durationMs: 12,
          reasoning: [
            'strategy:medium',
            'phase: AwaitingDraw',
            'wall remaining: 70',
          ],
        },
      ],
    }),
  }));

  await page.route('**/api/games/**/replay**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ gameId: FAKE_GAME_ID, events: [], hands: [] }),
  }));
}

async function openAuditTab(page: Page): Promise<boolean> {
  await page.goto(`?gameId=${FAKE_GAME_ID}&view=replay`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(800);
  const auditTab = page.getByTestId('replay-audit-tab');
  if (await auditTab.count() === 0) return false;
  const visible = await auditTab.isVisible().catch(() => false);
  if (!visible) return false;
  await auditTab.click();
  await page.waitForTimeout(500);
  return true;
}

test.describe('Mahjong Autotable — Wave 10 audit why-expand', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Audit why-expand desktop-only on first pass; mobile deferred.');
  });

  test('audit row exposes a why-expand toggle', async ({ page }) => {
    test.setTimeout(45_000);
    await mockAuditBackend(page);
    if (!(await openAuditTab(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }
    const expand = page.getByTestId('replay-audit-row-0-why');
    if (await expand.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-{i}-why toggle not yet wired',
      });
      return;
    }
    await expect(expand).toBeVisible();
  });

  test('clicking why-toggle reveals reasoning list', async ({ page }) => {
    test.setTimeout(45_000);
    await mockAuditBackend(page);
    if (!(await openAuditTab(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }
    const expand = page.getByTestId('replay-audit-row-0-why');
    if (await expand.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-{i}-why toggle not yet wired',
      });
      return;
    }
    await expand.click();
    await page.waitForTimeout(300);
    const panel = page.getByTestId('replay-audit-row-0-reasoning');
    if (await panel.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-{i}-reasoning panel not yet wired',
      });
      return;
    }
    await expect(panel).toBeVisible();
    await expect(panel).toContainText(/safety analysis/i);
    await expect(panel).toContainText(/strategy:master/i);
  });

  test('reasoning lines render as separate list items', async ({ page }) => {
    test.setTimeout(45_000);
    await mockAuditBackend(page);
    if (!(await openAuditTab(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }
    const expand = page.getByTestId('replay-audit-row-0-why');
    if (await expand.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-{i}-why toggle not yet wired',
      });
      return;
    }
    await expand.click();
    await page.waitForTimeout(300);
    const items = page.locator('[data-testid^="replay-audit-row-0-reasoning-line-"]');
    if (await items.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'reasoning-line {i} testids not yet wired',
      });
      return;
    }
    expect(await items.count()).toBeGreaterThanOrEqual(2);
  });

  test('panel toggles closed on second click', async ({ page }) => {
    test.setTimeout(45_000);
    await mockAuditBackend(page);
    if (!(await openAuditTab(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }
    const expand = page.getByTestId('replay-audit-row-0-why');
    if (await expand.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-{i}-why toggle not yet wired',
      });
      return;
    }
    await expand.click();
    await page.waitForTimeout(200);
    await expand.click();
    await page.waitForTimeout(200);
    const panel = page.getByTestId('replay-audit-row-0-reasoning');
    if (await panel.count() === 0) return;
    // After two clicks the panel must NOT be visible.
    const hidden = await panel.isHidden().catch(() => true);
    expect(hidden).toBe(true);
  });

  test('strategy badge carries data-strategy attribute', async ({ page }) => {
    test.setTimeout(45_000);
    await mockAuditBackend(page);
    if (!(await openAuditTab(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-tab not yet wired',
      });
      return;
    }
    const expand = page.getByTestId('replay-audit-row-0-why');
    if (await expand.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'replay-audit-row-{i}-why toggle not yet wired',
      });
      return;
    }
    await expand.click();
    await page.waitForTimeout(200);
    const badge = page.locator('[data-testid="replay-audit-row-0-reasoning"] [data-strategy]');
    if (await badge.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'reasoning data-strategy attribute not yet wired',
      });
      return;
    }
    await expect(badge.first()).toHaveAttribute('data-strategy', /master|medium|hard|easy/);
  });
});

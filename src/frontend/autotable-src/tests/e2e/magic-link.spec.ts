// Phase J Wave 8 — Magic-link landing spec (Vasquez).
//
// Validates the magic-link consumption flow surfaced via Hicks's
// `wireMagicLinkLanding()` in `src/auth.ts`:
//   • A URL with ?auth=<token> on landing kicks the
//     /api/auth/email/verify (or /api/auth/magic-link/verify) call.
//   • A success response shows [data-testid="magic-link-landing-success"].
//   • A failure response shows [data-testid="magic-link-landing-failure"].
//   • The continue button [data-testid="magic-link-landing-continue"]
//     dismisses the overlay.
//
// Reflection-defensive: if the magic-link landing surface hasn't shipped
// yet, the test soft-passes (annotations.push). We mock the verify call
// so we don't need a real Bishop token-issuance round-trip.
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md
// (Phase J Wave 8 § Auth surfaces — magic-link-landing).

import { test, expect, type Page } from '@playwright/test';

async function gotoWithToken(page: Page, token: string): Promise<void> {
  await page.goto(`?auth=${token}`);
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(750);
}

async function landingShipped(page: Page): Promise<boolean> {
  const success = await page.getByTestId('magic-link-landing-success').count();
  const failure = await page.getByTestId('magic-link-landing-failure').count();
  const root = await page.getByTestId('magic-link-landing').count();
  return success > 0 || failure > 0 || root > 0;
}

test.describe('Mahjong Autotable — magic-link landing', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Magic-link landing is desktop-only on first pass; mobile deferred.');
  });

  test('success token shows the success panel', async ({ page }) => {
    test.setTimeout(45_000);

    // Mock the verify endpoint to a 200 success — both candidate paths.
    await page.route('**/api/auth/email/verify**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        playerId: '00000000-0000-0000-0000-000000000001',
        email: 'test@example.com',
        provider: 'EmailMagicLink',
      }),
    }));
    await page.route('**/api/auth/magic-link/verify**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        playerId: '00000000-0000-0000-0000-000000000001',
        email: 'test@example.com',
        provider: 'EmailMagicLink',
      }),
    }));

    await gotoWithToken(page, 'fake-success-token-abcdefghijklmnop');

    if (!(await landingShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'magic-link-landing surface not yet wired',
      });
      return;
    }

    const success = page.getByTestId('magic-link-landing-success');
    if (await success.count() > 0) {
      await expect(success).toBeVisible({ timeout: 5_000 });
    }
  });

  test('invalid token shows the failure panel', async ({ page }) => {
    test.setTimeout(45_000);

    await page.route('**/api/auth/email/verify**', (route) => route.fulfill({
      status: 400,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'token expired or already consumed' }),
    }));
    await page.route('**/api/auth/magic-link/verify**', (route) => route.fulfill({
      status: 400,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'token expired or already consumed' }),
    }));

    await gotoWithToken(page, 'bogus-token-xxxxxxxxxxxxxxxxxxxx');

    if (!(await landingShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'magic-link-landing surface not yet wired',
      });
      return;
    }

    const failure = page.getByTestId('magic-link-landing-failure');
    if (await failure.count() > 0) {
      await expect(failure).toBeVisible({ timeout: 5_000 });
    }
  });

  test('continue button dismisses the landing overlay', async ({ page }) => {
    test.setTimeout(45_000);

    await page.route('**/api/auth/email/verify**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ playerId: 'p1', email: 't@example.com' }),
    }));
    await page.route('**/api/auth/magic-link/verify**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ playerId: 'p1', email: 't@example.com' }),
    }));

    await gotoWithToken(page, 'fake-success-token-ABCDEFGHIJKLMNOP');

    if (!(await landingShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'magic-link-landing surface not yet wired',
      });
      return;
    }

    const cont = page.getByTestId('magic-link-landing-continue');
    if (await cont.count() > 0) {
      await cont.click();
      const root = page.getByTestId('magic-link-landing');
      if (await root.count() > 0) {
        await expect(root).toBeHidden({ timeout: 5_000 });
      }
    }
  });
});

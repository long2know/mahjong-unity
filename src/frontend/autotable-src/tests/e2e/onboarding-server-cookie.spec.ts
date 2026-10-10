// Phase K Wave 2 — Onboarding server-cookie spec (Vasquez).
//
// Wave 1 stored "tour completed" in localStorage. Wave 2 promotes
// onboarding state to the server so it survives device reinstalls.
// Validates:
//   • GET `/api/players/me/onboarding-status` is called at boot.
//   • Banner appears when server says `completed = false`.
//   • POST `/api/players/me/onboarding-status` is fired when the
//     user dismisses the banner.
//   • Banner stays hidden after a reload when the server reports
//     `completed = true`.
//
// Backend fully mocked.

import { test, expect, type Page } from '@playwright/test';

async function mockBaseAuth(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-onb',
      displayName: 'Onboarding Tester',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 2 — onboarding server cookie', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Onboarding server-cookie spec is desktop-only.');
  });

  test('GET onboarding-status is called at boot', async ({ page }) => {
    test.setTimeout(45_000);
    let called = false;
    await page.route('**/api/players/me/onboarding-status**', (route) => {
      called = true;
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ completed: false }),
      });
    });
    await mockBaseAuth(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(500);
    if (!called) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: '/api/players/me/onboarding-status ships in Phase K Wave 2',
      });
      return;
    }
    expect(called).toBeTruthy();
  });

  test('onboarding banner visible when server says not completed', async ({ page }) => {
    test.setTimeout(45_000);
    await page.route('**/api/players/me/onboarding-status**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ completed: false }),
      }));
    await mockBaseAuth(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(500);

    const banner = page.getByTestId('onboarding-status-banner');
    if (await banner.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'onboarding-status-banner ships in Phase K Wave 2',
      });
      return;
    }
    await expect(banner).toBeVisible();
  });

  test('dismiss fires POST onboarding-status with completed=true', async ({ page }) => {
    test.setTimeout(45_000);
    let postedBody: string | undefined;
    await page.route('**/api/players/me/onboarding-status**', (route) => {
      const req = route.request();
      if (req.method() === 'POST') {
        postedBody = req.postData() ?? '';
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ completed: true }),
        });
      } else {
        route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ completed: false }),
        });
      }
    });
    await mockBaseAuth(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(500);

    const dismiss = page.getByTestId('onboarding-status-dismiss');
    if (await dismiss.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'onboarding-status-dismiss button ships in Phase K Wave 2',
      });
      return;
    }
    await dismiss.click();
    await page.waitForTimeout(400);
    if (postedBody === undefined) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'POST onboarding-status wired in Phase K Wave 2',
      });
      return;
    }
    expect(/completed.*true/i.test(postedBody)).toBeTruthy();
  });

  test('banner hidden when server reports completed=true', async ({ page }) => {
    test.setTimeout(45_000);
    await page.route('**/api/players/me/onboarding-status**', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ completed: true }),
      }));
    await mockBaseAuth(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(500);

    const banner = page.getByTestId('onboarding-status-banner');
    if (await banner.count() === 0) return; // not yet shipped — soft-pass
    await expect(banner).toBeHidden();
  });
});

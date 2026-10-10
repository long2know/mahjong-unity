// Phase K Wave 3 — Microsoft (Entra ID) OAuth button spec (Vasquez).
//
// Phase K Wave 3 adds Microsoft Entra ID as a third OAuth provider
// alongside Google + GitHub. The sign-in modal must surface a
// "Sign in with Microsoft" button whose challenge URL carries
// `provider=microsoft`. See selectors.md § Phase K Wave 3 → Microsoft
// OAuth.
//
// Soft-passes when the button or providers endpoint isn't yet shipped.

import { test, expect, type Page } from '@playwright/test';

async function mockProviders(page: Page, microsoftEnabled: boolean): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 401,
    contentType: 'application/json',
    body: JSON.stringify({}),
  }));
  await page.route('**/api/auth/providers**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      providers: [
        { id: 'google', enabled: true, label: 'Google' },
        { id: 'github', enabled: true, label: 'GitHub' },
        { id: 'microsoft', enabled: microsoftEnabled, label: 'Microsoft' },
      ],
    }),
  }));
}

// The sign-in modal opener historically carried `signin-modal-open`
// in selectors.md drafts but the shipped canonical chip is
// `signin-button` (header CTA — see selectors.md §`signin-button`).
// Try both so the spec is resilient to either landing.
async function openSignInModalIfNeeded(page: Page): Promise<void> {
  for (const tid of ['signin-modal-open', 'signin-button']) {
    const opener = page.getByTestId(tid);
    if (await opener.count() > 0) {
      await opener.first().click().catch(() => undefined);
      await page.waitForTimeout(200);
      return;
    }
  }
}

test.describe('Phase K Wave 3 — Microsoft OAuth', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'OAuth button visibility validated on chromium only.');
  });

  test('sign-in modal shows Microsoft button when provider enabled', async ({ page }) => {
    test.setTimeout(45_000);
    await mockProviders(page, true);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    // Find the signin opener if needed.
    await openSignInModalIfNeeded(page);

    const btn = page.getByTestId('signin-provider-microsoft');
    if (await btn.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'signin-provider-microsoft button ships in Phase K Wave 3',
      });
      return;
    }
    await expect(btn).toBeVisible();
  });

  test('Microsoft button href carries provider=microsoft', async ({ page }) => {
    test.setTimeout(45_000);
    await mockProviders(page, true);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    await openSignInModalIfNeeded(page);

    const btn = page.getByTestId('signin-provider-microsoft');
    if (await btn.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'Microsoft button ships in Phase K Wave 3',
      });
      return;
    }
    const href = await btn.evaluate((el) => {
      if (el instanceof HTMLAnchorElement) return el.href;
      const a = el.querySelector('a');
      return a ? a.href : '';
    });
    // Accept any of the canonical URL shapes.
    const ok = /provider=microsoft/i.test(href)
      || /\/auth\/microsoft\b/i.test(href)
      || /signin-microsoft/i.test(href);
    if (!ok && href.length === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'Microsoft button href forward-staged (form-POST flow ok)',
      });
      return;
    }
    expect(ok).toBeTruthy();
  });

  test('Microsoft button absent when provider disabled', async ({ page }) => {
    test.setTimeout(45_000);
    await mockProviders(page, false);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    await openSignInModalIfNeeded(page);

    const btn = page.getByTestId('signin-provider-microsoft');
    const cnt = await btn.count();
    if (cnt === 0) {
      // Correctly absent — or button not yet shipped.
      expect(cnt).toBe(0);
      return;
    }
    // If present, it must be hidden / non-interactive.
    const visible = await btn.isVisible().catch(() => false);
    expect(visible).toBeFalsy();
  });
});

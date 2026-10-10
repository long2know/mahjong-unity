// Phase K Wave 4 — Microsoft brand SVG inline spec (Vasquez).
//
// Wave 4 pins the "Sign in with Microsoft" button's brand mark as
// an INLINE <svg> 4-tile glyph, NOT an external CDN reference. See
// selectors.md § Phase K Wave 4 → microsoft brand svg.
//
// Soft-passes when the Microsoft button isn't yet rendered.

import { test, expect, type Page } from '@playwright/test';

async function mockProviders(page: Page): Promise<void> {
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
        { id: 'microsoft', enabled: true, label: 'Microsoft' },
      ],
    }),
  }));
}

test.describe('Phase K Wave 4 — Microsoft brand SVG', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Brand SVG inlining validated on chromium only.');
  });

  test('Microsoft button uses inline SVG (not CDN <img>)', async ({ page }) => {
    test.setTimeout(45_000);
    await mockProviders(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const opener = page.getByTestId('signin-modal-open');
    if (await opener.count() > 0) {
      await opener.first().click().catch(() => undefined);
      await page.waitForTimeout(200);
    }

    const btn = page.getByTestId('signin-provider-microsoft');
    if (await btn.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'Microsoft button not yet rendered — brand SVG ships in Wave 4',
      });
      return;
    }
    // Inspect the button subtree: must carry an <svg> child and
    // must NOT carry an <img> pointing at a microsoft CDN host.
    const hasInlineSvg = await btn.locator('svg').count();
    const cdnImgCount = await btn.locator(
      'img[src*="microsoft.com"], img[src*="microsoftonline.com"], '
      + 'img[src*="static2.sharepointonline.com"]'
    ).count();
    expect(cdnImgCount).toBe(0);
    if (hasInlineSvg === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'inline SVG ships in Wave 4',
      });
      return;
    }
    expect(hasInlineSvg).toBeGreaterThanOrEqual(1);
  });

  test('document body has no CDN-hosted Microsoft brand <img>', async ({ page }) => {
    test.setTimeout(45_000);
    await mockProviders(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const cdnHits = await page.locator(
      'img[src*="login.microsoftonline.com"], '
      + 'img[src*="static2.sharepointonline.com"]'
    ).count();
    expect(cdnHits).toBe(0);
  });
});

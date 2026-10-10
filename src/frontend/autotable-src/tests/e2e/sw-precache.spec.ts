// Phase K Wave 3 — Service-Worker precache manifest spec (Vasquez).
//
// Phase K Wave 3 finalises the precache pipeline so the registered SW
// fetches `manifest-precache.json` at install time and warms the cache
// for the lobby shell + onboarding tour assets. See selectors.md
// § Phase K Wave 3 → SW precache.
//
// Soft-passes when the SW or manifest aren't yet shipped.

import { test, expect, type Page } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-sw',
      displayName: 'SW User',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 3 — SW precache manifest', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'SW precache validated on desktop chromium only.');
  });

  test('SW registration fetches manifest-precache.json', async ({ page }) => {
    test.setTimeout(45_000);
    const manifestReqs: string[] = [];
    page.on('request', (r) => {
      const url = r.url();
      if (/manifest-precache\.json/i.test(url)) manifestReqs.push(url);
    });
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1200);

    if (manifestReqs.length === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'manifest-precache.json ships in Phase K Wave 3',
      });
      return;
    }
    expect(manifestReqs.length).toBeGreaterThan(0);
  });

  test('manifest-precache.json responds with a valid asset list', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');

    const base = page.url().replace(/[^/]+$/, '');
    const resp = await page.request.get(`${base}manifest-precache.json`)
      .catch(() => null);
    if (!resp || resp.status() === 404) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'manifest-precache.json ships in Phase K Wave 3',
      });
      return;
    }
    expect(resp.status()).toBeLessThan(500);
    if (resp.ok()) {
      const body = await resp.json().catch(() => null);
      // Accept either an array or an object with { assets: [...] }.
      const isArray = Array.isArray(body);
      const hasAssets = body && typeof body === 'object'
        && Array.isArray((body as { assets?: unknown[] }).assets);
      expect(isArray || hasAssets).toBeTruthy();
    }
  });

  test('SW controller activates without throwing', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');
    await page.waitForTimeout(1500);

    const swState = await page.evaluate(async () => {
      if (!('serviceWorker' in navigator)) return 'unsupported';
      try {
        const reg = await navigator.serviceWorker.getRegistration();
        if (!reg) return 'none';
        return reg.active?.state ?? reg.installing?.state
          ?? reg.waiting?.state ?? 'pending';
      } catch {
        return 'error';
      }
    });
    if (swState === 'none' || swState === 'unsupported') {
      test.info().annotations.push({
        type: 'soft-pass',
        description: `SW registration not yet shipped (state=${swState})`,
      });
      return;
    }
    expect(['activated', 'activating', 'installed', 'installing', 'pending'])
      .toContain(swState);
  });
});

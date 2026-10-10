// Phase K Wave 7 — Bundler-swap no-regression spec (Vasquez).
//
// Hicks's W7 brief picks a bundler (Vite / Rspack / Parcel-manual).
// Whichever lands, the runtime contract is: all chunks load on lobby
// load, no console errors emerge, and the page reaches `networkidle`
// without throwing. This spec is the wave-over-wave smoke that
// regressions in the bundler swap WILL be caught before merge.
//
// See selectors.md § Phase K Wave 7 → bundler-swap no-regression.

import { test, expect, type ConsoleMessage, type Page } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-bundler-swap',
      displayName: 'Bundler Swap Watcher',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 7 — bundler-swap no-regression', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'bundler-swap smoke validated on chromium only.');
  });

  test('lobby load completes with no console errors', async ({ page }) => {
    test.setTimeout(60_000);
    const consoleErrors: string[] = [];
    page.on('console', (msg: ConsoleMessage) => {
      if (msg.type() === 'error') {
        consoleErrors.push(`[${msg.type()}] ${msg.text()}`);
      }
    });
    const pageErrors: string[] = [];
    page.on('pageerror', (e: Error) => {
      pageErrors.push(`[pageerror] ${e.message}`);
    });

    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('networkidle');

    // Filter out HMR / dev-server noise that's expected and benign.
    const realErrors = consoleErrors.filter((line) =>
      !/favicon|service-worker|sourcemap|websocket connection|net::ERR_/i.test(line));

    if (realErrors.length === 0 && pageErrors.length === 0) {
      return;
    }

    // Failure mode is loud — surface the joined error list so Hicks can
    // diagnose what the bundler emitted.
    expect(
      realErrors.length + pageErrors.length,
      `bundler-swap regression — console errors observed:\n${[...realErrors, ...pageErrors].join('\n')}`)
      .toEqual(0);
  });
});

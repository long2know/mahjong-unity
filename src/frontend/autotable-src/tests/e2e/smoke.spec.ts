// Phase J Wave 5 — Playwright smoke spec (Apone, contract from Vasquez).
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md
// (Vasquez's Wave-4 stability document — kebab-case, surface-prefixed,
// guaranteed stable identity / cardinality / lifetime / naming).
//
// These tests are the framework-proving smoke layer for the integration
// suite. Deeper flows (full discard cycle, claim windows, reconnect-token
// round-trip) are owned by Vasquez and slot into separate spec files as
// follow-up waves land.
//
// Selector reality check: the four testids asserted below
// (`lobby-quick-match`, `lobby-open-settings`, `lobby-seat-preview-0`,
// `mobile-move-log-toggle`) are present in `index.html` at HEAD. Several
// other testids documented in selectors.md (`lobby-players-section`,
// `lobby-apply`, `lobby-variant-fieldset`, the `connection-banner-*`
// surface) are aspirational — Hicks's surface hasn't applied them yet.
// Apone scheduled a follow-up note in the Wave-5 memo so they can land
// in tandem with the next surface refactor.

import { test, expect } from '@playwright/test';

test.describe('Mahjong Autotable — smoke', () => {
  test('app loads with autotable title', async ({ page }) => {
    await page.goto('');
    // Upstream pwmarcz/autotable's index.html sets <title>Autotable</title>
    // (src/frontend/autotable-src/index.html:5). Keep the match loose
    // (case-insensitive substring) so a future title rebrand to e.g.
    // "Mahjong Autotable" doesn't break the smoke.
    await expect(page).toHaveTitle(/autotable/i);
  });

  test('lobby is reachable on first load', async ({ page }) => {
    await page.goto('');

    // selectors.md pins `lobby-quick-match` as the lobby's primary CTA;
    // its visibility is a positive signal that the lobby panel mounted
    // (it lives inside #lobby-panel.lobby-open, so the testid implies
    // both the panel exists and the open-class was applied).
    await expect(page.getByTestId('lobby-quick-match')).toBeVisible({ timeout: 10_000 });

    // The 4-cell seat preview is the only structural surface of the
    // lobby that has full testid coverage today; assert seat 0 (East)
    // is mounted as a second positive signal.
    await expect(page.getByTestId('lobby-seat-preview-0')).toBeVisible();
  });

  test('Quick Match starts a game shell', async ({ page }) => {
    await page.goto('');

    const quickMatch = page.getByTestId('lobby-quick-match');
    await expect(quickMatch).toBeVisible({ timeout: 10_000 });

    // Quick Match calls `window.location.replace(buildUrl(...))`
    // (src/lobby.ts:594–607).  buildUrl always emits `variant=`,
    // `botCount=`, and `handCount=` (src/lobby.ts:328–342), so the
    // post-click URL search string is guaranteed to be non-empty
    // whatever the picker defaults are — and the bare landing URL
    // has an empty search (shouldShowOnLoad() at lobby.ts:359–361).
    //
    // We dispatch the click via page.evaluate() (i.e. fire the JS
    // `click` event directly on the button element) rather than via
    // Playwright's pointer-events stack.  On mobile-chrome with
    // `isMobile: true` Playwright synthesises touch events that
    // interact unpredictably with the off-screen settings drawer at
    // `right: -340px, z-index: 1080` even when the visual stack is
    // correct.  The JS click event still fires the same handler
    // registered at lobby.ts:594 — verified via the URL transition
    // below — making this signal viewport-portable.
    await quickMatch.evaluate((el: HTMLElement) => el.click());

    await expect.poll(
      () => page.url(),
      { timeout: 20_000, message: 'expected URL to gain ?variant= after Quick Match click' },
    ).toMatch(/[?&]variant=/);
  });

  test('mobile drawer toggle is visible on Pixel 5', async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'mobile-chrome',
      'Mobile-only contract — selectors.md scopes mobile-move-log-toggle to the 768px breakpoint.'
    );

    await page.goto('');
    await expect(page.getByTestId('mobile-move-log-toggle')).toBeVisible();
  });
});

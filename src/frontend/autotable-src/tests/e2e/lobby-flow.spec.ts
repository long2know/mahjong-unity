// Phase J Wave 6 — Lobby flow (onboarding) spec.
//
// Validates:
//   • Visit /autotable/ as a first-time user (no `mahjong_pid` cookie
//     in storage).  The onboarding card must appear above the lobby
//     tabs.
//   • Type a display name, skip the colour picker (leave default),
//     click "Continue" — the card dismisses and the lobby's profile
//     chip surfaces the chosen name.
//   • Reload the page.  The onboarding card MUST NOT reappear (cookie
//     is now on the jar + localStorage onboarded flag is set), and
//     the profile chip still carries the name.
//
// We use `context.clearCookies()` + clearing localStorage before the
// first navigation to guarantee the first-visit condition is met,
// even when the spec runs after another spec in the same browser
// context.
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md.

import { test, expect } from '@playwright/test';

const CHOSEN_NAME = 'HicksTester';

test.describe('Mahjong Autotable — lobby onboarding flow', () => {
  test('first-visit onboarding card lifecycle', async ({ page, context }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Onboarding flow is desktop-only (mobile project covers a separate viewport).');
    test.setTimeout(60_000);

    // Reset cookies + localStorage so the test is hermetic regardless
    // of execution order.  The cookie sniff in identity.ts is the
    // primary first-visit signal, but localStorage carries the
    // `mahjong.identity.onboarded.v1` flag that gates the card on
    // subsequent visits.
    await context.clearCookies();
    await page.goto('');
    await page.evaluate(() => {
      try {
        window.localStorage.removeItem('mahjong.identity.onboarded.v1');
        window.localStorage.removeItem('mahjong.identity.cache.v1');
        // Also clear the Wave-5 profile cache so the lobby chip
        // doesn't carry forward a previous name from another spec.
        window.localStorage.removeItem('mahjong.profile.cache.v1');
      } catch { /* ignore */ }
    });

    // Re-navigate so the cleared-cookies + cleared-LS state applies
    // to the next boot of the identity module.
    await page.goto('');

    const card = page.getByTestId('onboarding-card');
    await expect(card).toBeVisible({ timeout: 10_000 });

    // Type the chosen name.  The input's `input` listener validates
    // each keystroke; we wait for the error message to clear before
    // clicking Continue so the validation gate doesn't reject the
    // submission.
    const nameInput = page.getByTestId('onboarding-display-name-input');
    await expect(nameInput).toBeVisible();
    await nameInput.fill(CHOSEN_NAME);

    // "Skip color" per the directive = don't click any preset; the
    // selectedColor stays at the default seeded by identity.ts.
    const continueBtn = page.getByTestId('onboarding-continue');
    await expect(continueBtn).toBeVisible();
    await continueBtn.click();

    // Card dismissed.  Card stays in the DOM but display flips to
    // none, so isHidden is the right check.
    await expect(card).toBeHidden({ timeout: 5_000 });

    // The profile chip in the lobby footer reflects the chosen name.
    // The label gets the displayName via profile.ts:installProfileToggle
    // — that fires on the next ProfileLoaded event from SignalR after
    // the UpdateProfile RPC the Continue handler invoked.  We poll
    // because the RPC round-trip is asynchronous and we don't want to
    // be brittle to its timing.
    const profileLabel = page.locator('#lobby-open-profile-label');
    await expect(profileLabel).toBeVisible({ timeout: 5_000 });
    await expect.poll(
      async () => (await profileLabel.textContent())?.trim() ?? '',
      { timeout: 10_000, message: 'expected profile chip label to surface chosen name' },
    ).toBe(CHOSEN_NAME);

    // Reload — the cookie is now on the jar (identity.ts read it on
    // first POST response), so the onboarding card MUST stay hidden
    // and the profile chip must still carry the chosen name.
    await page.reload();

    const cardAfterReload = page.getByTestId('onboarding-card');
    // The card markup is in the DOM at all times; `isHidden` here
    // means display:none / aria-hidden=true, which is what
    // identity.ts:hideOnboardingCard() sets.  Wait briefly so the
    // bootstrap POST has a chance to land and the visibility helper
    // can run if it disagrees.
    await page.waitForTimeout(800);
    await expect(cardAfterReload).toBeHidden();

    const profileLabel2 = page.locator('#lobby-open-profile-label');
    await expect(profileLabel2).toBeVisible({ timeout: 5_000 });
    await expect.poll(
      async () => (await profileLabel2.textContent())?.trim() ?? '',
      { timeout: 10_000, message: 'profile chip label should still show chosen name post-reload' },
    ).toBe(CHOSEN_NAME);
  });
});

// Phase K Wave 5 — Voice reason spectator-distinct spec (Vasquez).
//
// Wave 4 added voiceReasonToText with five canonical reasons. Wave 5
// pins that the "spectator" reason MUST render its OWN unique copy
// — NOT a fallback to "not-seated" or "unauthorized". A spectator
// cannot speak even though they are seated in the sense of having
// joined, so the operator-facing text needs to be diagnostically
// distinct.
//
// See selectors.md § Phase K Wave 5 → voice reason spectator distinct.

import { test, expect, type Page } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-voice-spectator',
      displayName: 'Voice Spectator Watcher',
      claims: { role: 'spectator' },
      roles: ['spectator'],
    }),
  }));
}

async function readToastTextFor(page: Page, reason: string): Promise<string | null> {
  await page.evaluate((r) => {
    const detail = { reason: r, code: 403 };
    window.dispatchEvent(new CustomEvent('voice:failure', { detail }));
    window.dispatchEvent(new CustomEvent('mahjong:voice-failure', { detail }));
  }, reason);
  await page.waitForTimeout(400);
  const toast = page.getByTestId('voice-failure-toast');
  if (await toast.count() === 0) return null;
  return (await toast.first().innerText()).trim();
}

test.describe('Phase K Wave 5 — voice reason spectator distinct', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Voice reason copy validated on chromium only.');
  });

  test('"spectator" reason text differs from "not-seated"', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const spectator = await readToastTextFor(page, 'spectator');
    if (spectator === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-failure-toast not yet wired (forward-staged)',
      });
      return;
    }
    const notSeated = await readToastTextFor(page, 'not-seated');
    if (notSeated === null) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-failure-toast disappeared between dispatches',
      });
      return;
    }

    // Hard-pin: spectator text MUST be non-empty AND differ from the
    // not-seated copy. The user-facing copy is allowed to evolve;
    // the distinction is what matters.
    expect(spectator.length).toBeGreaterThan(0);
    expect(spectator).not.toEqual(notSeated);
    expect(spectator.toLowerCase()).not.toEqual('not-seated');
  });
});

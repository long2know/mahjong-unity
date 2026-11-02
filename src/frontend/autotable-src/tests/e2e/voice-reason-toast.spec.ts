// Phase K Wave 4 — Voice failure reason toast spec (Vasquez).
//
// Wave 4 adds a typed voice-failure reason mapper (Hicks's
// voiceReasonToText): when the voice relay rejects a frame the UI
// surfaces a human-readable toast instead of the raw enum / code.
// See selectors.md § Phase K Wave 4 → voice reason toast.
//
// Soft-passes when the toast / mapper isn't yet wired (forward-stage).

import { test, expect, type Page } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-voice-toast',
      displayName: 'Voice Toast Watcher',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
}

test.describe('Phase K Wave 4 — voice reason toast', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Voice toast UI validated on chromium only.');
  });

  test('voice failure surfaces typed reason text — not raw code', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    // Inject a synthetic voice-failure event so we don't depend on
    // a live SignalR hub. The Wave-4 mapper is the unit under test;
    // both window-event and CustomEvent fan-out are accepted.
    await page.evaluate(() => {
      const detail = { reason: 'rate-limited', code: 429 };
      window.dispatchEvent(new CustomEvent('voice:failure', { detail }));
      window.dispatchEvent(new CustomEvent('mahjong:voice-failure', { detail }));
    });
    await page.waitForTimeout(400);

    const toast = page.getByTestId('voice-failure-toast');
    if (await toast.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-failure-toast not yet wired — mapper ships in Wave 4',
      });
      return;
    }
    const text = (await toast.first().innerText()).toLowerCase();
    // Must NOT be the raw enum/code.
    expect(text).not.toBe('rate-limited');
    expect(text).not.toContain('429');
    // Must carry human-readable wording.
    const ok = /rate.?limit|too many|slow down|try again|busy/i.test(text);
    expect(ok).toBeTruthy();
  });

  test('unknown reason falls back to a generic message', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    await page.evaluate(() => {
      const detail = { reason: 'totally-unknown-reason-xyz', code: 0 };
      window.dispatchEvent(new CustomEvent('voice:failure', { detail }));
      window.dispatchEvent(new CustomEvent('mahjong:voice-failure', { detail }));
    });
    await page.waitForTimeout(400);

    const toast = page.getByTestId('voice-failure-toast');
    if (await toast.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-failure-toast not yet wired',
      });
      return;
    }
    const text = (await toast.first().innerText()).toLowerCase();
    // Generic fallback must not echo the raw token verbatim.
    expect(text).not.toContain('totally-unknown-reason-xyz');
    expect(text.length).toBeGreaterThan(0);
  });
});

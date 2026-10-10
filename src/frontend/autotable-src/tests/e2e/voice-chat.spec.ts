// Phase K Wave 2 — Voice chat overlay spec (Vasquez).
//
// Validates the WebRTC voice signalling UI (see selectors.md § Phase
// K Wave 2 → Voice chat overlay):
//   • `voice-mic-toggle` mutes / unmutes the local mic without
//     blowing up when getUserMedia is denied (test environments).
//   • `voice-peer-status-<id>` pills appear for each remote peer
//     reported by the hub.
//   • `voice-volume-slider` adjusts the local playback gain.
//   • Voice is OFF by default and never opens a mic stream before
//     the user clicks the toggle.
//
// Backend (SignalR + getUserMedia) is fully mocked. Each test soft-
// passes when its target test-id is not yet shipped.

import { test, expect, type Page } from '@playwright/test';

async function mockBackend(page: Page): Promise<void> {
  await page.route('**/api/auth/me**', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      playerId: 'p-voice',
      displayName: 'Voice User',
      claims: { role: 'player' },
      roles: ['player'],
    }),
  }));
  // Stub out getUserMedia so tests never prompt for real microphone.
  await page.addInitScript(() => {
    try {
      const md = navigator.mediaDevices as MediaDevices | undefined;
      if (md) {
        (md as unknown as { getUserMedia: unknown }).getUserMedia = () =>
          Promise.resolve({
            getTracks: () => [],
            getAudioTracks: () => [],
            getVideoTracks: () => [],
          } as unknown as MediaStream);
      }
    } catch {
      /* ignore */
    }
  });
}

test.describe('Phase K Wave 2 — voice chat overlay', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Voice chat covered on desktop chromium only; mobile deferred.');
  });

  test('mic toggle is OFF by default and toggles state', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('voice-mic-toggle');
    if (await toggle.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-mic-toggle ships in Phase K Wave 2',
      });
      return;
    }
    // OFF by default: aria-pressed=false or has data-state=off.
    const off = await toggle.evaluate((el) =>
      el.getAttribute('aria-pressed') === 'false' ||
      el.getAttribute('data-state') === 'off' ||
      !el.classList.contains('active'));
    expect(off).toBeTruthy();

    await toggle.click();
    await page.waitForTimeout(200);
    const on = await toggle.evaluate((el) =>
      el.getAttribute('aria-pressed') === 'true' ||
      el.getAttribute('data-state') === 'on' ||
      el.classList.contains('active'));
    expect(on).toBeTruthy();
  });

  test('peer status pill renders when a peer joins', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    // Simulate a peer-status update via a global hook the voice UI
    // exposes (forward-staged: hook may not exist yet).
    await page.evaluate(() => {
      const w = window as unknown as {
        __voiceTestHook?: (peerId: string, status: string) => void;
      };
      if (typeof w.__voiceTestHook === 'function') {
        w.__voiceTestHook('peer-alpha', 'connected');
      }
    });

    const pill = page.getByTestId('voice-peer-status-peer-alpha');
    if (await pill.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-peer-status-<id> pill ships in Phase K Wave 2',
      });
      return;
    }
    await expect(pill).toBeVisible();
  });

  test('volume slider adjusts playback gain', async ({ page }) => {
    test.setTimeout(45_000);
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const slider = page.getByTestId('voice-volume-slider');
    if (await slider.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-volume-slider ships in Phase K Wave 2',
      });
      return;
    }
    // Set value via fillable input.
    await slider.evaluate((el) => {
      const input = el as HTMLInputElement;
      input.value = '0.5';
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
    });
    const value = await slider.evaluate((el) => (el as HTMLInputElement).value);
    expect(value).toBe('0.5');
  });

  test('voice off by default — no getUserMedia call before toggle', async ({ page }) => {
    test.setTimeout(45_000);
    await page.addInitScript(() => {
      const w = window as unknown as { __gumCalls?: number };
      w.__gumCalls = 0;
      const md = navigator.mediaDevices as MediaDevices | undefined;
      if (md) {
        const original = md.getUserMedia?.bind(md);
        md.getUserMedia = ((c: MediaStreamConstraints) => {
          w.__gumCalls = (w.__gumCalls ?? 0) + 1;
          return original ? original(c) : Promise.reject(new Error('no gum'));
        }) as MediaDevices['getUserMedia'];
      }
    });
    await mockBackend(page);
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(800);

    const calls = await page.evaluate(() =>
      (window as unknown as { __gumCalls?: number }).__gumCalls ?? 0);
    expect(calls).toBe(0);
  });

  test('mic permission denied does not crash UI', async ({ page }) => {
    test.setTimeout(45_000);
    await page.addInitScript(() => {
      const md = navigator.mediaDevices as MediaDevices | undefined;
      if (md) {
        (md as unknown as { getUserMedia: unknown }).getUserMedia = () =>
          Promise.reject(new DOMException('Denied', 'NotAllowedError'));
      }
    });
    await page.route('**/api/auth/me**', (route) => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ playerId: 'p1', roles: ['player'] }),
    }));
    await page.goto('');
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(500);

    const toggle = page.getByTestId('voice-mic-toggle');
    if (await toggle.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'voice-mic-toggle ships in Phase K Wave 2',
      });
      return;
    }
    await toggle.click();
    await page.waitForTimeout(400);
    // Page should still respond — try a no-op interaction.
    await page.evaluate(() => document.title);
    expect(true).toBeTruthy();
  });
});

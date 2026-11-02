// Phase J Wave 6 — Sound toggle persistence spec.
//
// Validates:
//   • Click [data-testid="lobby-open-settings"] to open the settings drawer.
//   • Click [data-testid="settings-sound"] to flip the sound toggle.
//   • Verify localStorage `mahjong:soundEnabled` flips from "true" → "false".
//   • Reload the page; verify the key + checkbox state both persist.
//
// The mirror that writes `mahjong:soundEnabled` lives in lobby.ts
// (installSoundEnabledMirror); the Wave-3 settings drawer keeps the
// canonical JSON payload at `autotable.phaseJ.v1.settings.*`.  This
// spec validates the Wave-6 mirror, not the JSON payload — the
// underlying behavior is already proven by the Wave-3 manual QA.
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md.

import { test, expect, type Page } from '@playwright/test';

const SOUND_LS_KEY = 'mahjong:soundEnabled';

// The Wave-7 V2 drawer is tab-gated — the sound checkbox lives in
// the Audio tab.  Hicks (e2e wave 27) wired `lobby-open-settings`
// to open the V2 drawer; this helper hops to the Audio tab so
// `[data-testid="settings-sound"]` becomes interactable.
async function activateAudioTab(page: Page): Promise<void> {
  // Wait for the V2 drawer to lazy-mount its tab strip; the lobby's
  // settings shortcut forwards into a dynamic-imported chunk, so the
  // tab may not be in the DOM on the next tick.  We bail to a no-op
  // only if the tab never surfaces (legacy build, etc).
  const tab = page.getByTestId('settings-tab-audio');
  await tab.waitFor({ state: 'attached', timeout: 5_000 }).catch(() => undefined);
  if (await tab.count() === 0) return;
  if ((await tab.getAttribute('aria-selected')) === 'true') {
    await page.getByTestId('settings-panel-audio').waitFor({ state: 'visible', timeout: 5_000 }).catch(() => undefined);
    return;
  }
  await tab.click();
  await expect(page.getByTestId('settings-panel-audio')).toBeVisible({ timeout: 5_000 });
}

test.describe('Mahjong Autotable — sound toggle persistence', () => {
  test('toggles flip the mahjong:soundEnabled key and persist across reload', async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Settings drawer surface is desktop-only on first pass.');
    test.setTimeout(60_000);

    await page.goto('');

    // Open the settings drawer via the lobby shortcut.  The drawer
    // markup ships in index.html at all viewports — only the visual
    // overlay is gated behind `.settings-open`.
    const openSettings = page.getByTestId('lobby-open-settings');
    await expect(openSettings).toBeVisible({ timeout: 10_000 });
    await openSettings.click();

    // Wait for the sound checkbox.  It exists in the DOM at all
    // times; we just need to wait for the drawer to be in the
    // .settings-open state so the click target is interactable.
    await activateAudioTab(page);
    const soundToggle = page.getByTestId('settings-sound');
    await expect(soundToggle).toBeVisible();

    // Read the initial state directly from the checkbox + the LS
    // mirror to confirm they agree at boot.  installSoundEnabledMirror
    // writes the key synchronously inside initLobby so by the time
    // openSettings is visible the key must exist.
    const initialChecked = await soundToggle.isChecked();
    const initialLs = await page.evaluate(
      (k) => window.localStorage.getItem(k), SOUND_LS_KEY);
    expect(initialLs).toBe(initialChecked ? 'true' : 'false');

    // Flip the toggle and verify the LS key flipped to match.
    await soundToggle.click();
    await expect.poll(
      () => page.evaluate(
        (k) => window.localStorage.getItem(k), SOUND_LS_KEY),
      { timeout: 5_000, message: 'expected mahjong:soundEnabled to flip after toggle' },
    ).toBe(initialChecked ? 'false' : 'true');

    const afterToggleChecked = await soundToggle.isChecked();
    expect(afterToggleChecked).toBe(!initialChecked);

    // Reload the page and confirm both the LS key and the checkbox
    // state survive.  The settings drawer hydrates on boot from the
    // existing JSON payload (`autotable.phaseJ.v1.settings.*`) before
    // installSoundEnabledMirror runs, so the checkbox lands in the
    // expected post-flip state.
    await page.reload();

    // Re-open the drawer post-reload.
    const openSettings2 = page.getByTestId('lobby-open-settings');
    await expect(openSettings2).toBeVisible({ timeout: 10_000 });
    await openSettings2.click();

    await activateAudioTab(page);
    const soundToggle2 = page.getByTestId('settings-sound');
    await expect(soundToggle2).toBeVisible();

    const reloadedChecked = await soundToggle2.isChecked();
    expect(reloadedChecked).toBe(!initialChecked);

    const reloadedLs = await page.evaluate(
      (k) => window.localStorage.getItem(k), SOUND_LS_KEY);
    expect(reloadedLs).toBe(initialChecked ? 'false' : 'true');

    // Flip back so the next test invocation in the same browser tab
    // doesn't carry forward a sticky off state.
    await soundToggle2.click();
    await expect.poll(
      () => page.evaluate(
        (k) => window.localStorage.getItem(k), SOUND_LS_KEY),
      { timeout: 5_000 },
    ).toBe(initialChecked ? 'true' : 'false');
  });
});

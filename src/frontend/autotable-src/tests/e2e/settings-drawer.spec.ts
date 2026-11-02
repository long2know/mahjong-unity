// Phase J Wave 7 — Settings drawer spec (Vasquez).
//
// Validates the Wave-3 settings drawer surface (re-skinned in Wave 7
// with a tabbed v2 layout):
//   • [data-testid="lobby-open-settings"] click → drawer opens.
//   • Drawer renders tabs in [data-testid="settings-drawer"].
//   • [data-testid="settings-sound"] (toggle) is reachable inside the
//     drawer panels — i.e. the drawer's tab panel container actually
//     surfaces the per-tab controls rather than rendering only the tab
//     strip.
//   • [data-testid="settings-save"] click → confirmation note becomes
//     visible (the `#settings-saved-note-v2` element flips display:none
//     → inline-block per settings.ts:flashSaved()).
//   • [data-testid="settings-reset"] click → values revert to their
//     defaults; the sound toggle returns to its boot state.
//   • [data-testid="settings-close"] click → drawer closes (the
//     #app-settings-drawer-v2 element loses the .settings-open class).
//   • Save → reload → drawer state persists (the localStorage payload
//     at `autotable.phaseJ.v1.settings.*` round-trips).
//
// The spec is desktop-only on the first pass — Bootstrap's responsive
// off-canvas behaviour on mobile breakpoints would force a layout
// branch the drawer surface doesn't fully implement until Wave 8.
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md.

import { test, expect, type Page } from '@playwright/test';

async function openSettingsDrawer(page: Page): Promise<void> {
  await page.goto('');
  const open = page.getByTestId('lobby-open-settings');
  await expect(open).toBeVisible({ timeout: 10_000 });
  await open.click();
  await expect(page.getByTestId('settings-drawer')).toBeVisible();
}

// The Wave-7 V2 drawer is tab-gated — only the active tab's panel
// is rendered (the others have `hidden`).  The sound checkbox lives
// in the Audio tab, so this helper activates it before any
// assertion that needs the sound input visible.
async function activateAudioTab(page: Page): Promise<void> {
  // Wait for the V2 drawer's tab strip to lazy-mount before clicking.
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

test.describe('Mahjong Autotable — settings drawer', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Settings drawer surface is desktop-only on first pass.');
  });

  test('opens, exposes save/reset/close buttons, and closes via X', async ({ page }) => {
    test.setTimeout(45_000);
    await openSettingsDrawer(page);

    // Each of the documented affordances must be reachable.
    const close = page.getByTestId('settings-close');
    const save = page.getByTestId('settings-save');
    const reset = page.getByTestId('settings-reset');
    await expect(close).toBeVisible();
    await expect(save).toBeVisible();
    await expect(reset).toBeVisible();

    // Close via the X — drawer should hide.
    await close.click();
    await expect(page.locator('#settings-drawer-v2'))
      .not.toHaveClass(/settings-drawer-v2-open/);
  });

  test('save then reload — drawer state persists in localStorage', async ({ page }) => {
    // We don't rely on the legacy `mahjong:soundEnabled` mirror (covered
    // by sound-toggle.spec.ts) — instead we round-trip the canonical
    // payload at autotable.phaseJ.v1.settings.* via the save button and
    // assert the keys survive a navigation.
    test.setTimeout(60_000);
    await openSettingsDrawer(page);

    // Toggle the sound checkbox first to mark the form dirty.
    await activateAudioTab(page);
    const sound = page.getByTestId('settings-sound');
    await expect(sound).toBeVisible();
    const before = await sound.isChecked();
    await sound.click();
    await expect(sound).toBeChecked({ checked: !before });

    // Save and verify the confirmation note flashes.
    await page.getByTestId('settings-save').click();
    const savedNote = page.locator('#settings-saved-note-v2');
    await expect(savedNote).toBeVisible({ timeout: 5_000 });

    // Snapshot the LS payload, reload, re-open the drawer, and
    // assert the toggle state survived the round-trip.
    const persistedKeys = await page.evaluate(() => {
      const out: Record<string, string | null> = {};
      for (let i = 0; i < window.localStorage.length; i++) {
        const k = window.localStorage.key(i);
        if (!k) continue;
        if (k.startsWith('autotable.phaseJ.') ||
            k.startsWith('mahjong:soundEnabled')) {
          out[k] = window.localStorage.getItem(k);
        }
      }
      return out;
    });
    expect(Object.keys(persistedKeys).length).toBeGreaterThan(0);

    await page.reload();
    const reopen = page.getByTestId('lobby-open-settings');
    await expect(reopen).toBeVisible({ timeout: 10_000 });
    await reopen.click();
    await activateAudioTab(page);
    const soundAfter = page.getByTestId('settings-sound');
    await expect(soundAfter).toBeVisible();
    await expect(soundAfter).toBeChecked({ checked: !before });
  });

  test('reset reverts dirty form to defaults', async ({ page }) => {
    test.setTimeout(45_000);
    await openSettingsDrawer(page);

    await activateAudioTab(page);
    const sound = page.getByTestId('settings-sound');
    await expect(sound).toBeVisible();
    const before = await sound.isChecked();

    // Flip the toggle to mark the form dirty …
    await sound.click();
    await expect(sound).toBeChecked({ checked: !before });

    // … then reset.  The toggle must spring back to its boot value.
    await page.getByTestId('settings-reset').click();
    await expect(sound).toBeChecked({ checked: before });
  });
});

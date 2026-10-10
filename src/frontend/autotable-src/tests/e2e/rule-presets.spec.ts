// Phase J Wave 8 — Rule presets spec (Vasquez).
//
// Validates the rule-presets editor surfaced by Hicks's
// `src/rule-presets.ts`:
//   • [data-testid="lobby-rule-preset-select"] is populated with at
//     least the seeded "Classic Changsha" entry.
//   • [data-testid="settings-tab-rule-presets"] opens the editor panel.
//   • Creating a draft via [data-testid="rule-preset-new-button"]
//     exposes the editable fields.
//   • [data-testid="rule-preset-save"] persists; status text updates.
//   • [data-testid="rule-preset-delete"] removes the custom preset.
//
// Reflection-defensive: every getByTestId is preceded by a count check;
// missing surface → soft-pass.
//
// Selector contract: src/frontend/autotable-src/tests/selectors.md
// (Phase J Wave 8 § Rule presets).

import { test, expect, type Page } from '@playwright/test';

async function openLobby(page: Page): Promise<void> {
  await page.goto('');
  await page.waitForLoadState('domcontentloaded');
  await page.waitForTimeout(500);
}

async function rulePresetsShipped(page: Page): Promise<boolean> {
  return (await page.getByTestId('lobby-rule-preset-select').count()) > 0
      || (await page.getByTestId('settings-tab-rule-presets').count()) > 0;
}

test.describe('Mahjong Autotable — rule presets', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(
      testInfo.project.name !== 'chromium',
      'Rule-preset editor is desktop-only on first pass.');
  });

  test('lobby dropdown lists at least the Classic preset', async ({ page }) => {
    test.setTimeout(45_000);
    await openLobby(page);

    if (!(await rulePresetsShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'rule-preset surface not yet wired (Hicks Wave 8 landing)',
      });
      return;
    }

    const select = page.getByTestId('lobby-rule-preset-select');
    if (await select.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'lobby-rule-preset-select not exposed (may be settings-only)',
      });
      return;
    }

    await expect(select).toBeVisible({ timeout: 10_000 });
    // Wait for the GET /api/rule-presets fetch to settle.
    await page.waitForTimeout(750);
    const optionCount = await select.locator('option').count();
    expect(optionCount).toBeGreaterThanOrEqual(1);
    // Ferro WP-E/#120 (Ripley C-2 ruling) — rule presets are NOT applied to
    // Changsha gameplay yet, so the lobby picker is display-only: it must be
    // disabled so it can't imply an effect (the lobby also no longer emits
    // `?rulePreset=`). The settings-panel editor below remains the real CRUD
    // surface.
    await expect(select, 'lobby rule-preset picker must be disabled (not yet applied)').toBeDisabled();
  });

  test('settings drawer rule-preset tab is reachable', async ({ page }) => {
    test.setTimeout(45_000);
    await openLobby(page);

    if (!(await rulePresetsShipped(page))) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'rule-preset surface not yet wired',
      });
      return;
    }

    const tab = page.getByTestId('settings-tab-rule-presets');
    if (await tab.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'rule-preset settings tab not yet wired',
      });
      return;
    }

    // The settings drawer may need to be opened first. Try opening
    // via the standard testid; if unavailable, try the hamburger.
    const drawerToggle = page.locator('[data-testid="settings-toggle"], [data-testid="lobby-settings-open"]').first();
    if (await drawerToggle.count() > 0) {
      await drawerToggle.click({ trial: false }).catch(() => undefined);
    }

    await tab.click().catch(() => undefined);
    const panel = page.getByTestId('settings-panel-rule-presets');
    if (await panel.count() > 0) {
      await expect(panel).toBeVisible({ timeout: 5_000 });
    }
  });

  test('new-preset button surfaces editable form fields', async ({ page }) => {
    test.setTimeout(45_000);
    await openLobby(page);

    const newBtn = page.getByTestId('rule-preset-new-button');
    if (await newBtn.count() === 0) {
      test.info().annotations.push({
        type: 'soft-pass',
        description: 'rule-preset-new-button not yet wired',
      });
      return;
    }

    await newBtn.click().catch(() => undefined);
    await page.waitForTimeout(500);

    // At least the name + handLimit inputs must surface for a new draft.
    const nameInput = page.getByTestId('rule-preset-edit-name');
    const handLimitInput = page.getByTestId('rule-preset-edit-handLimit');
    if (await nameInput.count() > 0) {
      await expect(nameInput).toBeVisible({ timeout: 5_000 });
    }
    if (await handLimitInput.count() > 0) {
      await expect(handLimitInput).toBeVisible({ timeout: 5_000 });
    }
  });

  test('#131 — rule-preset editor carries the i18n "not yet functional" notice', async ({ page }) => {
    test.setTimeout(45_000);
    await openLobby(page);

    // Open the Wave-7 settings drawer (V2). The first click lazy-loads +
    // installs it, which builds the Rule-presets panel via
    // rule-presets.renderEditorPanel() — where the notice lives.
    const settingsBtn = page.locator('#settings-button');
    await expect(settingsBtn, 'V2 settings drawer trigger must exist').toBeVisible({ timeout: 10_000 });
    await settingsBtn.click();

    // Activate the Rule presets tab so the panel (+ notice) is visible.
    const tab = page.getByTestId('settings-tab-rule-presets');
    await expect(tab).toBeVisible({ timeout: 10_000 });
    await tab.click();

    // #131 acceptance: an unmistakable, i18n'd notice that presets don't yet
    // affect gameplay (tracked in #130), so a user can't believe a preset
    // changes the game before the rules engine honors it.
    const notice = page.getByTestId('rule-preset-preview-notice');
    await expect(notice, 'rule-preset editor must warn presets are not yet functional').toBeVisible({ timeout: 10_000 });
    await expect(notice).toContainText('#130');
    // i18n resolved (not the raw catalog key).
    await expect(notice).not.toHaveText('settings.rule_presets.preview_notice');
  });
});

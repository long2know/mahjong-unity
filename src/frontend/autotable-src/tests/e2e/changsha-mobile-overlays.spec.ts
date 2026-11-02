import { test, expect, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { newActor, applyRoom, closeLobby, dismissPrompts, probe, driveManualCeremony } from './_lobby-repair';
import { mobileGeometry, waitForBoardHand } from './_mobile-geometry';

async function visibleHand(page: Page, count = 14): Promise<void> {
  const layout = await mobileGeometry(page);
  expect(layout.tiles).toHaveLength(count);
  expect(layout.pageWidth).toBeLessThanOrEqual(layout.width);
  expect(layout.pageHeight).toBeLessThanOrEqual(layout.height);
  for (const tile of layout.tiles) {
    expect(tile.width).toBeGreaterThanOrEqual(10);
    expect(tile.height).toBeGreaterThanOrEqual(16);
    expect(tile.hit, `tile ${tile.id} must still receive the touch`).toBe(true);
  }
  const handTop = Math.min(...layout.tiles.map(tile => tile.top));
  for (const selector of ['#chat-panel:not(.chat-panel-collapsed)', 'body.move-log-open #move-log']) {
    const panel = page.locator(selector);
    if (await panel.count() === 0) continue;
    const bounds = await panel.boundingBox();
    expect(bounds, selector).not.toBeNull();
    expect(bounds!.y + bounds!.height < handTop
      || bounds!.x > Math.max(...layout.tiles.map(tile => tile.right)), `${selector} clears the on-board hand`).toBe(true);
    await expect(panel).toBeInViewport({ ratio: 1 });
  }
  await expect(page.locator('#turn-banner')).toBeVisible();
}

async function statusPreference(page: Page, on: boolean): Promise<void> {
  await page.getByTestId('settings-button').click();
  await expect(page.getByTestId('settings-drawer')).toBeVisible();
  await page.getByTestId('settings-tab-display').click();
  await page.getByTestId('settings-mobile-table-status').setChecked(on);
  await page.getByTestId('settings-close').click();
}

test('prewarmed mobile settings opens on one real click, closes and reopens normally', async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'first-settings-click', context);
    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await waitForBoardHand(actor.page);
    await actor.page.getByTestId('settings-button').hover();
    await expect(actor.page.getByTestId('settings-tab-display')).toBeAttached();
    await actor.page.getByTestId('settings-button').click();
    await expect(actor.page.getByTestId('settings-drawer')).toBeVisible();
    await actor.page.getByTestId('settings-close').click();
    await expect(actor.page.getByTestId('settings-drawer')).toBeHidden();
    await actor.page.getByTestId('settings-button').tap();
    await expect(actor.page.getByTestId('settings-drawer')).toBeVisible();
    await actor.page.getByTestId('settings-close').click();
    await visibleHand(actor.page);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

async function storedPanel(page: Page): Promise<unknown> {
  return page.evaluate(() => JSON.parse(localStorage.getItem('mahjong.settings.v1') ?? '{}').mobileInfoPanel);
}

for (const viewport of [{ width: 390, height: 844 }, { width: 360, height: 800 }, { width: 844, height: 390 }]) {
  test(`compact overlay defaults, on-demand panels and persistent choice ${viewport.width}x${viewport.height}`, async ({ browser, baseURL }, testInfo) => {
    test.setTimeout(60_000); // Reload twice plus both real overlay controls in one unchanged hand.
    const context = await browser.newContext({ viewport, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
    try {
      const actor = await newActor(browser, baseURL, testInfo, 'overlay-prefs', context);
      await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
      await applyRoom(actor, 3, 0, 'auto');
      await closeLobby(actor);
      await waitForBoardHand(actor.page);
      const handBefore = (await probe(actor)).handIds;
      const commandStart = actor.sent.length;
      await expect(actor.page.locator('#bot-banner')).toBeHidden();
      await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect(actor.page.locator('#move-log-toggle')).toHaveAttribute('aria-expanded', 'false');
      await visibleHand(actor.page);

      await statusPreference(actor.page, true);
      await expect(actor.page.locator('#bot-banner .bot-banner-summary')).toHaveText('1 human · 3 bots · 0 open');
      await expect(actor.page.locator('#bot-banner')).toBeVisible();
      await actor.page.reload({ waitUntil: 'domcontentloaded' });
      await dismissPrompts(actor);
      await closeLobby(actor);
      await waitForBoardHand(actor.page);
      await expect(actor.page.locator('#bot-banner')).toBeVisible();
      await statusPreference(actor.page, false);
      await expect(actor.page.locator('#bot-banner')).toBeHidden();

      await actor.page.locator('#move-log-toggle').tap();
      await expect(actor.page.locator('#move-log')).toBeInViewport({ ratio: 1 });
      await expect.poll(() => storedPanel(actor.page)).toBe('move-log');
      await visibleHand(actor.page);
      await actor.page.reload({ waitUntil: 'domcontentloaded' });
      await dismissPrompts(actor);
      await closeLobby(actor);
      await waitForBoardHand(actor.page);
      await expect(actor.page.locator('#move-log-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(actor.page.locator('#bot-banner')).toBeHidden();
      await visibleHand(actor.page);

      await actor.page.getByTestId('chat-toggle').tap();
      await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'true');
      await expect(actor.page.locator('#move-log-toggle')).toHaveAttribute('aria-expanded', 'false');
      await expect.poll(() => storedPanel(actor.page)).toBe('chat');
      await visibleHand(actor.page);
      const report = await mobileGeometry(actor.page);
      const output = testInfo.outputPath('open-chat-hand.json');
      mkdirSync(dirname(output), { recursive: true });
      writeFileSync(output, JSON.stringify(report, null, 2));
      await actor.page.screenshot({ path: testInfo.outputPath('open-chat-hand.png') });
      expect((await probe(actor)).handIds).toEqual(handBefore);
      expect(actor.sent.slice(commandStart).flatMap(frame => frame.entries ?? [])
        .filter(([kind]) => ['things', 'discard', 'claim', 'match'].includes(kind))).toEqual([]);

      const tile = report.tiles[4];
      const discardStart = actor.sent.length;
      await actor.page.touchscreen.tap(tile.x, tile.y);
      await expect.poll(() => actor.sent.slice(discardStart).flatMap(frame => frame.entries ?? [])
        .filter(([kind]) => kind === 'discard').map(([, , info]) => info?.tileId)).toEqual([tile.id]);
      await expect.poll(async () => (await probe(actor)).handIds).not.toContain(tile.id);
      expect(actor.errors).toEqual([]);
    } finally { await context.close(); }
  });
}

test('mobile overlay choice does not hide desktop status or overwrite desktop chat preference', async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'desktop-overlay-prefs', context);
    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await expect.poll(async () => (await probe(actor)).handCount).toBe(14);
    await expect(actor.page.locator('#bot-banner')).toBeVisible();
    await actor.page.getByTestId('chat-toggle').click();
    await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'true');
    await actor.page.setViewportSize({ width: 390, height: 844 });
    await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'false');
    await expect(actor.page.locator('#bot-banner')).toBeHidden();
    await actor.page.locator('#move-log-toggle').tap();
    await expect.poll(() => storedPanel(actor.page)).toBe('move-log');
    await actor.page.setViewportSize({ width: 1280, height: 900 });
    await expect(actor.page.locator('#bot-banner')).toBeVisible();
    await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'true');
    await actor.page.setViewportSize({ width: 390, height: 844 });
    await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'false');
    await expect(actor.page.locator('#move-log-toggle')).toHaveAttribute('aria-expanded', 'true');
    await visibleHand(actor.page);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

test('open mobile chat leaves the real manual pickup actuator and partial hand reachable', async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'pickup-with-chat', context);
    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await applyRoom(actor, 3, 0, 'manual');
    await closeLobby(actor);
    await actor.page.locator('#roll-dice').tap();
    await expect.poll(async () => {
      const current = await probe(actor);
      return current.pickup?.seatIndex === 0 && current.pickup.count === 4;
    }).toBe(true);
    await actor.page.locator('#pickup-take-btn').tap();
    await waitForBoardHand(actor.page, 4);
    await actor.page.getByTestId('chat-toggle').tap();
    await visibleHand(actor.page, 4);
    await expect.poll(async () => {
      const current = await probe(actor);
      return current.pickup?.seatIndex === 0 && current.pickup.count === 4;
    }).toBe(true);
    await expect(actor.page.locator('#pickup-take-btn')).toBeVisible();
    expect(await actor.page.evaluate(() => {
      const button = document.getElementById('pickup-take-btn')!;
      const r = button.getBoundingClientRect();
      return button.contains(document.elementFromPoint((r.left + r.right) / 2, (r.top + r.bottom) / 2));
    })).toBe(true);
    await actor.page.locator('#pickup-take-btn').tap();
    await waitForBoardHand(actor.page, 8);
    await visibleHand(actor.page, 8);
    await driveManualCeremony([actor]);
    await waitForBoardHand(actor.page);
    await visibleHand(actor.page);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

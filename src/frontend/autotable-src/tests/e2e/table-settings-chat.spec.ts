import { test, expect, type Page } from '@playwright/test';
import { newActor, applyRoom, closeLobby, dismissPrompts, probe } from './_lobby-repair';
import { mobileGeometry, waitForBoardHand } from './_mobile-geometry';

async function authority(page: Page): Promise<unknown> {
  return page.evaluate(() => {
    const game = (window as unknown as { game: {
      client: { things: { entries(): Iterable<[string | number, unknown]> } };
      world: { things: Map<number, { slot: { name: string; thing?: { index: number } }; sent: boolean }> };
    } }).game;
    return { wire: [...game.client.things.entries()],
      slots: [...game.world.things].map(([id, tile]) => ({ id, slot: tile.slot.name, occupant: tile.slot.thing?.index, sent: tile.sent })) };
  });
}

async function selectOrder(page: Page, drawer: '#settings-drawer' | '#settings-panel-display', mode: 'suit' | 'groups'): Promise<void> {
  await page.locator(drawer).getByRole('button', { name: 'Hand order', exact: true }).click();
  await page.getByRole('option', { name: mode === 'suit' ? 'Suit + rank' : 'Pairs / triples first', exact: true }).click();
}

for (const viewport of [
  { width: 1280, height: 900 }, { width: 390, height: 844 }, { width: 844, height: 390 },
]) {
  test(`original Bot Strength Settings drawer controls the actual hand and camera ${viewport.width}x${viewport.height}`, async ({ browser, baseURL }, info) => {
    test.setTimeout(60_000);
    const mobile = viewport.width <= 900;
    const context = await browser.newContext({ viewport, hasTouch: mobile, isMobile: mobile, deviceScaleFactor: 1 });
    try {
      const actor = await newActor(browser, baseURL, info, 'original-gear', context);
      const page = actor.page;
      await page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
      await page.locator('#lobby-advanced summary').click();
      await page.locator('#lobby-seed').fill('4100');
      await applyRoom(actor, 3, 0, 'auto');
      await closeLobby(actor);
      await waitForBoardHand(page);
      const before = await authority(page), url = page.url(), mark = actor.sent.length;
      const original = (await mobileGeometry(page)).tiles;
      const suit = original.map(tile => tile.id);
      const groups = original.slice().sort((a, b) => {
        const count = (face: number): number => original.filter(tile => tile.face === face).length;
        return Number(count(b.face) > 1) - Number(count(a.face) > 1) || a.face - b.face || a.id - b.id;
      }).map(tile => tile.id);
      expect(groups).not.toEqual(suit);
      await page.getByRole('button', { name: 'Open per-game settings', exact: true }).click();
      const drawer = page.locator('#settings-drawer');
      await expect(drawer).toHaveAttribute('aria-hidden', 'false');
      await expect(drawer.locator('#settings-bot-strength')).toBeAttached();
      await expect(drawer.locator('#settings-hand-count')).toBeAttached();
      await expect(drawer.locator('#settings-auto-deal')).toBeAttached();
      await expect(drawer.locator('#settings-sound')).toBeAttached();
      const perspective = drawer.getByRole('checkbox', { name: 'Perspective view', exact: true });
      await expect(perspective.locator('..')).toBeInViewport({ ratio: 1 });
      await expect(drawer.locator('#settings-close')).toBeInViewport({ ratio: 1 });
      await page.screenshot({ path: info.outputPath('original-settings-drawer.png') });
      await perspective.uncheck();
      await expect.poll(async () => (await mobileGeometry(page)).camera).toBe('OrthographicCamera');
      await selectOrder(page, '#settings-drawer', 'groups');
      await drawer.locator('#settings-close').click();
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(groups);
      await expect(page.locator('#perspective')).not.toBeChecked();
      expect(await authority(page)).toEqual(before);
      expect(page.url(), 'display preferences do not Apply & Restart').toBe(url);

      await page.getByTestId('settings-button').click();
      await page.getByTestId('settings-tab-display').click();
      await expect(page.getByTestId('settings-hand-sort')).toHaveValue('groups');
      await expect(page.getByTestId('settings-perspective-toggle')).not.toBeChecked();
      await selectOrder(page, '#settings-panel-display', 'suit');
      await page.getByTestId('settings-perspective-toggle').check();
      await page.getByTestId('settings-close').click();
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(suit);
      await page.locator('#settings-toggle').click();
      await expect(page.getByTestId('settings-game-hand-sort')).toHaveValue('suit');
      await expect(page.getByTestId('settings-game-perspective')).toBeChecked();
      await selectOrder(page, '#settings-drawer', 'groups');
      await drawer.locator('#settings-close').click();
      await page.keyboard.press('p');
      await expect.poll(async () => (await mobileGeometry(page)).camera).toBe('OrthographicCamera');
      await expect(page.getByTestId('settings-game-perspective')).not.toBeChecked();
      await expect(page.getByTestId('settings-perspective-toggle')).not.toBeChecked();
      expect(await authority(page)).toEqual(before);
      expect(actor.sent.slice(mark).flatMap(frame => frame.entries ?? [])
        .filter(([kind]) => ['things', 'slots', 'discard', 'claim', 'match', 'seats'].includes(kind))).toEqual([]);

      await page.reload({ waitUntil: 'domcontentloaded' });
      await dismissPrompts(actor);
      await closeLobby(actor);
      await waitForBoardHand(page);
      await expect.poll(async () => (await mobileGeometry(page)).camera).toBe('OrthographicCamera');
      await expect.poll(async () => (await mobileGeometry(page)).tiles.map(tile => tile.id)).toEqual(groups);
      await page.locator('#settings-toggle').click();
      await expect(page.getByTestId('settings-game-perspective')).not.toBeChecked();
      await expect(page.getByTestId('settings-game-hand-sort')).toHaveValue('groups');
      await perspective.check();
      await drawer.locator('#settings-close').click();
      await expect.poll(async () => (await mobileGeometry(page)).camera).toBe('PerspectiveCamera');
      await page.screenshot({ path: info.outputPath('sorted-board-after-settings.png') });
      const tile = (await mobileGeometry(page)).tiles[4];
      expect(tile.hit).toBe(true);
      const discardStart = actor.sent.length;
      if (mobile) await page.touchscreen.tap(tile.x, tile.y);
      else await page.mouse.click(tile.x, tile.y);
      await expect.poll(() => actor.sent.slice(discardStart).flatMap(frame => frame.entries ?? [])
        .filter(([kind]) => kind === 'discard').map(([, , value]) => value?.tileId)).toEqual([tile.id]);
      await expect.poll(async () => (await probe(actor)).handIds).not.toContain(tile.id);
      expect(actor.errors).toEqual([]);
    } finally { await context.close(); }
  });
}

test('desktop chat has a real collapse gadget, keyboard reopen, retained messages and independent persistence', async ({ browser, baseURL }, info) => {
  test.setTimeout(60_000);
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, info, 'desktop-chat-collapse', context);
    const page = actor.page;
    await page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await waitForBoardHand(page);
    const toggle = page.getByTestId('chat-toggle'), close = page.getByTestId('chat-collapse');
    await expect(toggle).toHaveAccessibleName('Open chat');
    await toggle.click();
    await expect(close).toHaveAccessibleName('Collapse chat');
    await expect(close).toBeInViewport({ ratio: 1 });
    const input = page.getByTestId('chat-input');
    await expect(input).toBeEnabled();
    const message = `p q z — chat keyboard ${info.workerIndex}`;
    await input.pressSequentially(message);
    expect(await page.evaluate(() => {
      const game = (window as unknown as { game: { zoom: { pos: number }; lookDown: { pos: number }; mainView: { camera: { type: string } } } }).game;
      return { zoom: game.zoom.pos, lookDown: game.lookDown.pos, camera: game.mainView.camera.type };
    })).toEqual({ zoom: 0, lookDown: 0, camera: 'PerspectiveCamera' });
    await page.getByTestId('chat-send').click();
    await expect(page.getByTestId('chat-messages')).toContainText(message);
    await page.screenshot({ path: info.outputPath('desktop-chat-open.png') });
    await close.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toBeFocused();
    await expect(page.locator('#chat-panel-body')).toBeHidden();
    await expect(close).toBeHidden();
    await page.screenshot({ path: info.outputPath('desktop-chat-collapsed.png') });
    await toggle.press('Enter');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByTestId('chat-messages')).toContainText(message);
    await close.focus();
    await page.keyboard.down('Space');
    await page.waitForTimeout(200); // Hold a real button key long enough to detect accidental camera look-down.
    expect(await page.evaluate(() =>
      (window as unknown as { game: { lookDown: { pos: number } } }).game.lookDown.pos)).toBe(0);
    await page.keyboard.up('Space');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(await page.evaluate(() => localStorage.getItem('mahjong.chat.collapsed.v1'))).toBe('true');
    await page.reload({ waitUntil: 'domcontentloaded' });
    await dismissPrompts(actor);
    await closeLobby(actor);
    await waitForBoardHand(page);
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(toggle).toHaveAccessibleName('Open chat');
    await toggle.click();
    await expect(page.getByTestId('chat-messages')).toContainText(message);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

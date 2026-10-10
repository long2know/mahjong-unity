import { test, expect, type Page, type TestInfo } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { newActor, applyRoom, closeLobby, dismissPrompts, probe, type Actor } from './_lobby-repair';
import { mobileGeometry, setHandOrder, waitForBoardHand, type GeometryReport } from './_mobile-geometry';

type Rect = { left: number; top: number; right: number; bottom: number };
const overlaps = (a: Rect, b: Rect): boolean =>
  Math.min(a.right, b.right) > Math.max(a.left, b.left) + 0.5
  && Math.min(a.bottom, b.bottom) > Math.max(a.top, b.top) + 0.5;

async function view(page: Page, perspective: boolean): Promise<void> {
  await page.getByTestId('settings-button').click();
  await page.getByTestId('settings-tab-display').click();
  await page.getByTestId('settings-perspective-toggle').setChecked(perspective);
  await page.getByTestId('settings-close').click();
  await expect.poll(async () => (await mobileGeometry(page)).camera).toBe(perspective ? 'PerspectiveCamera' : 'OrthographicCamera');
}

async function overlays(page: Page): Promise<Array<Rect & { id: string }>> {
  return page.evaluate(() => {
    const result = [];
    for (const id of ['bot-banner', 'turn-banner', 'dice-hud', 'move-log', 'chat-panel', 'sidebar']) {
      const element = document.getElementById(id);
      if (!element || !element.getClientRects().length || getComputedStyle(element).visibility === 'hidden') continue;
      const r = element.getBoundingClientRect();
      if (r.left >= innerWidth || r.right <= 0 || r.top >= innerHeight || r.bottom <= 0) continue;
      result.push({ id, left: r.left, right: r.right, top: r.top, bottom: r.bottom });
    }
    const claim = document.querySelector('.ferro-claim-overlay-visible');
    if (claim?.getClientRects().length) {
      const r = claim.getBoundingClientRect();
      result.push({ id: 'claim-overlay', left: r.left, right: r.right, top: r.top, bottom: r.bottom });
    }
    return result;
  });
}

async function capture(page: Page, testInfo: TestInfo, name: string, handCount: number): Promise<GeometryReport> {
  await expect(page.getByTestId('hand-result-dialog')).toBeHidden();
  await expect.poll(async () => (await mobileGeometry(page)).tiles.length).toBe(handCount);
  const report = await mobileGeometry(page);
  const panels = await overlays(page);
  const file = testInfo.outputPath(name + '.json');
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify({ report, panels, excludedSurfaces: 'None: this is a live playable hand, not a paused result modal.' }, null, 2));
  await page.screenshot({ path: testInfo.outputPath(name + '.png') });
  // Keep every predicate, but do not create thousands of traced expect steps
  // per image. CI's retry trace spent31s in this synchronous assertion loop.
  const violations: string[] = [];
  const check = (ok: boolean, message: string): void => { if (!ok) violations.push(message); };
  check(report.pageWidth <= report.width, `page width ${report.pageWidth} exceeds ${report.width}`);
  check(report.pageHeight <= report.height, `page height ${report.pageHeight} exceeds ${report.height}`);
  for (const item of [...report.meshes, ...report.areas]) {
    check(item.left >= report.playArea.left - 0.5, `${item.slot}: left ${item.left}`);
    check(item.right <= report.playArea.right + 0.5, `${item.slot}: right ${item.right}`);
    check(item.top >= report.playArea.top - 0.5, `${item.slot}: top ${item.top}`);
    check(item.bottom <= report.playArea.bottom + 0.5, `${item.slot}: bottom ${item.bottom}`);
  }
  for (const item of [...report.meshes, ...report.tiles]) {
    for (const panel of panels) check(!overlaps(item, panel), `${panel.id} covers rendered tile ${item.id}`);
  }
  for (const tile of report.tiles) {
    check(tile.hit === true, `own tile ${tile.id} must receive the hit`);
    const compact = report.width <= 900 || report.height <= 520;
    check(tile.width >= (compact ? 10 : 30), `own tile ${tile.id}: width ${tile.width}`);
    check(tile.height >= (compact ? 16 : 44), `own tile ${tile.id}: height ${tile.height}`);
  }
  for (const action of report.actions) check(action.hit === true, `essential action ${action.id}`);
  expect(violations, `${name}: every projected bound, occlusion, touch size and action target`).toEqual([]);
  return report;
}

async function discard(actor: Actor, mode: 'suit' | 'groups'): Promise<number> {
  await setHandOrder(actor.page, mode);
  const before = actor.sent.length;
  const tile = (await mobileGeometry(actor.page)).tiles.at(-1)!;
  const id = tile.id;
  expect(tile.hit).toBe(true);
  await actor.page.touchscreen.tap(tile.x, tile.y);
  await expect.poll(() => actor.sent.slice(before).flatMap(frame => frame.entries ?? [])
    .filter(([kind]) => kind === 'discard').map(([, , value]) => value?.tileId)).toEqual([id]);
  await expect.poll(async () => (await probe(actor)).handIds).not.toContain(id);
  return id;
}

async function ownMeldCount(page: Page): Promise<number> {
  return page.evaluate(() => {
    const client = (window as unknown as { game: { client: {
      seat: number; things: { entries(): Iterable<[string | number, { slotName: string }]> };
    } } }).game.client;
    return [...client.things.entries()].filter(([, tile]) => tile.slotName.startsWith('meld.') && tile.slotName.endsWith('@' + client.seat)).length;
  });
}

async function reachOwnTurn(actor: Actor, wantMeld = false, testInfo?: TestInfo): Promise<void> {
  const deadline = Date.now() + 70_000;
  const passed = new Set<string>();
  while (Date.now() < deadline) {
    const settled = await actor.page.evaluate(() => (window as unknown as { game: { client: { result: { get(key: string): unknown } } } }).game.client.result.get('current'));
    if (settled) throw new Error('Hand settled before the requested live visual state; result modal intentionally pauses play and is not bypassed.');
    const state = await probe(actor);
    const claim = await actor.page.evaluate(() => {
      const c = (window as unknown as { game: { client: {
        seat: number; claim: { get(key: string): { available: string[] } | null };
      } } }).game.client;
      return c.claim.get(String(c.seat));
    });
    if (claim) {
      const key = JSON.stringify(claim);
      if (!passed.has(key)) {
        const selector = wantMeld && claim.available.includes('Pung')
          ? '.ferro-claim-overlay-visible .ferro-claim-badge-pung'
          : '.ferro-claim-overlay-visible .ferro-claim-pass';
        const button = actor.page.locator(selector);
        if (await button.isVisible()) {
          if (selector.endsWith('badge-pung') && testInfo) {
            await capture(actor.page, testInfo, 'claim-before-real-pung-390x844', state.handCount);
          }
          await button.click({ timeout: 1200 });
          passed.add(key);
          if (selector.endsWith('badge-pung')) {
            await expect.poll(() => ownMeldCount(actor.page)).toBeGreaterThanOrEqual(3);
          }
        }
      }
    } else if (state.turn?.awaitingDiscard && state.turn.activeSeat === state.seat) {
      if (!wantMeld || await ownMeldCount(actor.page) >= 3) return;
      await discard(actor, 'groups');
    }
    await actor.page.waitForTimeout(80);
  }
  throw new Error('Requested live draw/meld state was not reached within one bounded hand.');
}

test('final integrated visual gate: real draw, meld, both sorts, enabled panels, rotation and reconnect', async ({ browser, baseURL }, testInfo) => {
  test.setTimeout(180_000);
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'final-visual', context);
    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await actor.page.locator('#lobby-advanced summary').click();
    await actor.page.locator('#lobby-seed').fill('4100');
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await waitForBoardHand(actor.page);
    const sizes = [[360, 800], [390, 844], [844, 390], [820, 1180], [1280, 900]];
    for (const [width, height] of sizes) {
      await actor.page.setViewportSize({ width, height });
      for (const perspective of [true, false]) {
        await view(actor.page, perspective);
        await capture(actor.page, testInfo, `dealt-${width}x${height}-${perspective ? 'perspective' : 'flat'}`, 14);
      }
    }
    await actor.page.setViewportSize({ width: 390, height: 844 });
    await actor.page.getByTestId('settings-button').click();
    await actor.page.getByTestId('settings-tab-display').click();
    await actor.page.getByTestId('settings-mobile-table-status').check();
    await actor.page.getByTestId('settings-close').click();
    for (const [width, height] of sizes.slice(0, 3)) {
      await actor.page.setViewportSize({ width, height });
      for (const perspective of [true, false]) {
        await view(actor.page, perspective);
        await actor.page.getByTestId('chat-toggle').tap();
        await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'true');
        await capture(actor.page, testInfo, `chat-${width}x${height}-${perspective ? 'perspective' : 'flat'}`, 14);
        await actor.page.getByTestId('chat-toggle').tap();
        await actor.page.locator('#move-log-toggle').tap();
        await expect(actor.page.locator('#move-log')).toBeInViewport({ ratio: 1 });
        await capture(actor.page, testInfo, `log-${width}x${height}-${perspective ? 'perspective' : 'flat'}`, 14);
        await actor.page.locator('#move-log-toggle').tap();
      }
    }
    await actor.page.setViewportSize({ width: 390, height: 844 });
    await view(actor.page, true);
    const suitDiscard = await discard(actor, 'suit');
    await reachOwnTurn(actor);
    await capture(actor.page, testInfo, 'after-draw-suit-390x844', 14);
    const groupsDiscard = await discard(actor, 'groups');
    await reachOwnTurn(actor, true, testInfo);
    const concealed = (await probe(actor)).handCount;
    const melds = await ownMeldCount(actor.page);
    expect(concealed).toBe(11);
    expect(melds).toBe(3);
    for (const [width, height] of sizes) {
      await actor.page.setViewportSize({ width, height });
      for (const perspective of [true, false]) {
        await view(actor.page, perspective);
        await capture(actor.page, testInfo, `meld-${width}x${height}-${perspective ? 'perspective' : 'flat'}`, concealed);
      }
    }
    await actor.page.setViewportSize({ width: 390, height: 844 });
    await actor.page.reload({ waitUntil: 'domcontentloaded' });
    await actor.page.waitForFunction(() => {
      const game = (window as unknown as { game?: {
        gameUi?: unknown; client?: { connected(): boolean; seat: number | null };
      } }).game;
      return !!game?.gameUi && game.client?.connected() && game.client.seat === 0;
    });
    await dismissPrompts(actor);
    await closeLobby(actor);
    expect(await actor.page.evaluate(() => JSON.parse(localStorage.getItem('mahjong.settings.v1')!).handSort)).toBe('groups');
    await capture(actor.page, testInfo, 'meld-reconnected-390x844', concealed);
    writeFileSync(testInfo.outputPath('real-actions.json'), JSON.stringify({
      suitDiscard, groupsDiscard, concealed, exposedMeldTiles: melds,
      effectiveHandTiles: concealed + melds,
      explanation: 'After a Pung, 11 concealed + 3 exposed = 14 physical tiles. Concealed tiles remain individually touchable; exposed melds are correctly read-only.',
      sent: actor.sent, errors: actor.errors,
    }, null, 2));
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});

for (const viewport of [{ width: 360, height: 800 }, { width: 390, height: 844 }, { width: 844, height: 390 }]) {
for (const perspective of [true, false]) {
test(`final visual gate: manual wall-pickup with chat ${viewport.width}x${viewport.height} ${perspective ? 'perspective' : 'flat'}`, async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'final-wall-pickup', context);
    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await applyRoom(actor, 3, 0, 'manual');
    await closeLobby(actor);
    await view(actor.page, perspective);
    await actor.page.locator('#roll-dice').tap();
    await expect.poll(async () => (await probe(actor)).pickup?.seatIndex).toBe(0);
    await actor.page.getByTestId('chat-toggle').tap();
    await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'true');
    const report = await capture(actor.page, testInfo, 'manual-wall-pickup-chat', 0);
    const target = await actor.page.evaluate(() => {
      const c = (window as unknown as { game: { client: { pickup: { get(key: string): { targetSlots: string[] } } } } }).game.client;
      return c.pickup.get('current').targetSlots[0];
    });
    const tile = report.meshes.find(mesh => mesh.slot === target)!;
    expect(tile, 'server-designated real top wall tile must be rendered').toBeTruthy();
    const { x, y } = await actor.page.evaluate(slotName => {
      const game = (window as unknown as { game: {
        mainView: { camera: import('three').Camera };
        world: { slots: Map<string, import('../../src/slot').Slot> };
      } }).game;
      const place = game.world.slots.get(slotName)!.placeWithOffset(0);
      const top = place.position.clone();
      top.z += place.size.z / 2;
      top.project(game.mainView.camera);
      const canvas = document.querySelector('#main canvas')!.getBoundingClientRect();
      return { x: canvas.left + (top.x + 1) * canvas.width / 2, y: canvas.top + (1 - top.y) * canvas.height / 2 };
    }, target);
    expect(await actor.page.evaluate(({ x, y }) => document.elementFromPoint(x, y)?.tagName, { x, y })).toBe('CANVAS');
    await actor.page.touchscreen.tap(x, y);
    writeFileSync(testInfo.outputPath('wall-tap-observation.json'), JSON.stringify({
      target, x, y, sent: actor.sent,
      pointer: await actor.page.evaluate(() => {
        const g = (window as unknown as { game: {
          mouseUi: { mouse2: unknown; mouse3: unknown };
          world: { hovered: { index: number; slot: { name: string } } | null };
        } }).game;
        return { mouse2: g.mouseUi.mouse2, mouse3: g.mouseUi.mouse3, hovered: g.world.hovered?.slot.name ?? null };
      }),
    }, null, 2));
    await waitForBoardHand(actor.page, 4);
    await capture(actor.page, testInfo, 'manual-after-real-wall-tap', 4);
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});
}
}

for (const viewport of [{ width: 360, height: 800 }, { width: 390, height: 844 }, { width: 844, height: 390 }]) {
test(`CURRENT open-panel-only visual proof ${viewport.width}x${viewport.height}`, async ({ browser, baseURL }, testInfo) => {
  const context = await browser.newContext({ viewport, isMobile: true, hasTouch: true, deviceScaleFactor: 1 });
  try {
    const actor = await newActor(browser, baseURL, testInfo, 'current-panel-proof', context);
    await actor.page.locator('#lobby-hand-count-fieldset label:has(input[value="1"])').click();
    await applyRoom(actor, 3, 0, 'auto');
    await closeLobby(actor);
    await waitForBoardHand(actor.page);
    for (const perspective of [true, false]) {
      await view(actor.page, perspective);
      const suffix = `${viewport.width}x${viewport.height}-${perspective ? 'perspective' : 'flat'}`;
      await actor.page.getByTestId('chat-toggle').tap();
      await expect(actor.page.getByTestId('chat-toggle')).toHaveAttribute('aria-expanded', 'true');
      await capture(actor.page, testInfo, `CURRENT-chat-${suffix}`, 14);
      await actor.page.getByTestId('chat-toggle').tap();
      await actor.page.locator('#move-log-toggle').tap();
      await expect(actor.page.locator('#move-log')).toBeInViewport({ ratio: 1 });
      await capture(actor.page, testInfo, `CURRENT-log-${suffix}`, 14);
      await actor.page.locator('#move-log-toggle').tap();
    }
    expect(actor.errors).toEqual([]);
  } finally { await context.close(); }
});
}
